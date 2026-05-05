import * as mediasoup from 'mediasoup';
import * as amqp from 'amqplib';
import { v4 as uuidv4 } from 'uuid';

// ─── Configuration ───────────────────────────────────────────────
const RABBITMQ_URL = process.env.RABBITMQ_URL || 'amqp://guest:guest@rabbitmq:5672';
const AUDIO_CHUNKS_QUEUE = process.env.AUDIO_CHUNKS_QUEUE || 'audio_chunks';
const MEDIASOUP_PORT = parseInt(process.env.MEDIASOUP_PORT || '3000', 10);
const MEDIASOUP_ANNOUNCED_IP = process.env.MEDIASOUP_ANNOUNCED_IP || '127.0.0.1';

// ─── Types ───────────────────────────────────────────────────────
interface Room {
    id: string;
    router: mediasoup.types.Router;
    audioProducer?: mediasoup.types.Producer;
    audioConsumers: Map<string, mediasoup.types.Consumer>;
    participants: Map<string, mediasoup.types.WebRtcTransport>;
}

// ─── State ───────────────────────────────────────────────────────
const rooms = new Map<string, Room>();
let worker: mediasoup.types.Worker;
let amqpConnection: amqp.Connection;
let amqpChannel: amqp.Channel;

// ─── Mediasoup Setup ─────────────────────────────────────────────
async function createWorker(): Promise<mediasoup.types.Worker> {
    const worker = await mediasoup.createWorker({
        logLevel: 'warn',
        logTags: ['info', 'ice', 'dtls', 'rtp', 'srtp', 'rtcp'],
        rtcMinPort: 40000,
        rtcMaxPort: 49999,
    });

    worker.on('died', () => {
        console.error('Mediasoup worker died, exiting...');
        process.exit(1);
    });

    return worker;
}

async function createWebRtcTransport(router: mediasoup.types.Router): Promise<mediasoup.types.WebRtcTransport> {
    const transport = await router.createWebRtcTransport({
        listenIps: [{ ip: '0.0.0.0', announcedIp: MEDIASOUP_ANNOUNCED_IP }],
        enableUdp: true,
        enableTcp: true,
        preferUdp: true,
        initialAvailableOutgoingBitrate: 1000000,
    });

    return transport;
}

// ─── Room Management ─────────────────────────────────────────────
async function createRoom(roomId: string): Promise<Room> {
    const router = await worker.createRouter({
        mediaCodecs: [
            {
                kind: 'audio',
                mimeType: 'audio/opus',
                clockRate: 48000,
                channels: 2,
                parameters: {
                    useinbandfec: 1,
                },
            },
        ],
    });

    const room: Room = {
        id: roomId,
        router,
        audioConsumers: new Map(),
        participants: new Map(),
    };

    rooms.set(roomId, room);
    console.log(`Room created: ${roomId}`);
    return room;
}

async function closeRoom(roomId: string): Promise<void> {
    const room = rooms.get(roomId);
    if (!room) return;

    room.audioProducer?.close();
    room.audioConsumers.forEach(consumer => consumer.close());
    room.participants.forEach(transport => transport.close());
    room.router.close();
    rooms.delete(roomId);
    console.log(`Room closed: ${roomId}`);
}

// ─── RabbitMQ Integration ────────────────────────────────────────
async function connectRabbitMQ(): Promise<void> {
    amqpConnection = await amqp.connect(RABBITMQ_URL);
    amqpChannel = await amqpConnection.createChannel();
    await amqpChannel.assertQueue(AUDIO_CHUNKS_QUEUE, { durable: true });
    console.log(`Connected to RabbitMQ, queue: ${AUDIO_CHUNKS_QUEUE}`);
}

async function publishAudioChunk(roomId: string, participantId: string, data: Buffer): Promise<void> {
    const message = JSON.stringify({
        roomId,
        participantId,
        data: data.toString('base64'),
        timestamp: Date.now(),
    });

    amqpChannel.sendToQueue(AUDIO_CHUNKS_QUEUE, Buffer.from(message), {
        persistent: true,
    });
}

// ─── API Handlers (called from .NET backend via HTTP) ────────────
export async function createRoomHandler(roomId: string): Promise<any> {
    const room = await createRoom(roomId);
    return { roomId: room.id };
}

export async function joinRoomHandler(roomId: string, participantId: string): Promise<any> {
    const room = rooms.get(roomId);
    if (!room) throw new Error(`Room ${roomId} not found`);

    const transport = await createWebRtcTransport(room.router);
    room.participants.set(participantId, transport);

    return {
        transportOptions: {
            id: transport.id,
            iceParameters: transport.iceParameters,
            iceCandidates: transport.iceCandidates,
            dtlsParameters: transport.dtlsParameters,
        },
    };
}

export async function produceAudioHandler(
    roomId: string,
    participantId: string,
    dtlsParameters: any
): Promise<any> {
    const room = rooms.get(roomId);
    if (!room) throw new Error(`Room ${roomId} not found`);

    const transport = room.participants.get(participantId);
    if (!transport) throw new Error(`Transport for ${participantId} not found`);

    await transport.connect({ dtlsParameters });

    const producer = await transport.produce({
        kind: 'audio',
        rtpParameters: {},
    });

    room.audioProducer = producer;

    producer.on('transportclose', () => {
        producer.close();
    });

    // Forward audio data to RabbitMQ
    producer.on('score', (score) => {
        // Audio data is forwarded via the transport's RTP stream
        // In production, you'd capture the RTP packets and forward them
    });

    return { producerId: producer.id };
}

export async function consumeAudioHandler(
    roomId: string,
    participantId: string,
    rtpCapabilities: any
): Promise<any> {
    const room = rooms.get(roomId);
    if (!room) throw new Error(`Room ${roomId} not found`);

    if (!room.router.canConsume(rtpCapabilities)) {
        throw new Error('Cannot consume audio');
    }

    const transport = room.participants.get(participantId);
    if (!transport) throw new Error(`Transport for ${participantId} not found`);

    const consumer = await transport.consume({
        producerId: room.audioProducer!.id,
        rtpCapabilities,
        paused: false,
    });

    room.audioConsumers.set(participantId, consumer);

    consumer.on('transportclose', () => {
        room.audioConsumers.delete(participantId);
        consumer.close();
    });

    return {
        consumerId: consumer.id,
        producerId: consumer.producerId,
        kind: consumer.kind,
        rtpParameters: consumer.rtpParameters,
    };
}

export async function leaveRoomHandler(roomId: string, participantId: string): Promise<void> {
    const room = rooms.get(roomId);
    if (!room) return;

    const transport = room.participants.get(participantId);
    if (transport) {
        transport.close();
        room.participants.delete(participantId);
    }

    room.audioConsumers.delete(participantId);

    if (room.participants.size === 0) {
        await closeRoom(roomId);
    }
}

// ─── HTTP Server for .NET Backend Communication ──────────────────
import * as http from 'http';

const server = http.createServer(async (req, res) => {
    res.setHeader('Content-Type', 'application/json');

    try {
        const body = await getRequestBody(req);
        const { method, params } = JSON.parse(body);

        let result: any;

        switch (method) {
            case 'createRoom':
                result = await createRoomHandler(params.roomId);
                break;
            case 'joinRoom':
                result = await joinRoomHandler(params.roomId, params.participantId);
                break;
            case 'produceAudio':
                result = await produceAudioHandler(params.roomId, params.participantId, params.dtlsParameters);
                break;
            case 'consumeAudio':
                result = await consumeAudioHandler(params.roomId, params.participantId, params.rtpCapabilities);
                break;
            case 'leaveRoom':
                await leaveRoomHandler(params.roomId, params.participantId);
                result = { success: true };
                break;
            default:
                res.writeHead(400);
                res.end(JSON.stringify({ error: `Unknown method: ${method}` }));
                return;
        }

        res.writeHead(200);
        res.end(JSON.stringify(result));
    } catch (error: any) {
        console.error('Error handling request:', error);
        res.writeHead(500);
        res.end(JSON.stringify({ error: error.message }));
    }
});

function getRequestBody(req: http.IncomingMessage): Promise<string> {
    return new Promise((resolve, reject) => {
        let body = '';
        req.on('data', (chunk) => (body += chunk));
        req.on('end', () => resolve(body));
        req.on('error', reject);
    });
}

// ─── Main ────────────────────────────────────────────────────────
async function main() {
    console.log('Starting Mediasoup SFU server...');

    worker = await createWorker();
    console.log('Mediasoup worker created');

    await connectRabbitMQ();

    server.listen(MEDIASOUP_PORT, () => {
        console.log(`Mediasoup HTTP server listening on port ${MEDIASOUP_PORT}`);
    });

    // Handle graceful shutdown
    process.on('SIGTERM', async () => {
        console.log('SIGTERM received, shutting down...');
        await amqpChannel.close();
        await amqpConnection.close();
        worker.close();
        process.exit(0);
    });
}

main().catch((error) => {
    console.error('Fatal error:', error);
    process.exit(1);
});

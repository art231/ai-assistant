import { Injectable } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MicrophoneService } from './microphone.service';
import { Device, types } from 'mediasoup-client';
import type {
  DtlsParameters,
  MediaKind,
  RtpParameters,
  ConnectionState,
} from 'mediasoup-client/lib/types';

export interface MediasoupTransportOptions {
  id: string;
  iceParameters: any;
  iceCandidates: any[];
  dtlsParameters: any;
}

export interface ProducerInfo {
  producerId: string;
}

export interface ConsumerInfo {
  consumerId: string;
  producerId: string;
  kind: string;
  rtpParameters: any;
}

export type MediasoupConnectionState = 'disconnected' | 'connecting' | 'connected' | 'error';

@Injectable({
  providedIn: 'root',
})
export class MediasoupService {
  private mediasoupUrl = environment.mediasoupUrl;
  private device: Device | null = null;
  private sendTransport: types.Transport | null = null;
  private producer: types.Producer | null = null;

  private connectionStateSubject = new BehaviorSubject<MediasoupConnectionState>('disconnected');
  private errorSubject = new BehaviorSubject<string | null>(null);

  connectionState$: Observable<MediasoupConnectionState> = this.connectionStateSubject.asObservable();
  error$: Observable<string | null> = this.errorSubject.asObservable();

  get isConnected(): boolean {
    return this.connectionStateSubject.value === 'connected';
  }

  constructor(private microphoneService: MicrophoneService) {}

  /**
   * Call a method on the Mediasoup HTTP API.
   */
  private async callMediasoupApi(method: string, params: any): Promise<any> {
    const response = await fetch(`${this.mediasoupUrl}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ method, params }),
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({ error: 'Unknown error' }));
      throw new Error(error.error || `Mediasoup API error: ${response.status}`);
    }

    return await response.json();
  }

  /**
   * Create a room in Mediasoup (idempotent - safe to call multiple times).
   */
  async createRoom(roomId: string): Promise<void> {
    try {
      await this.callMediasoupApi('createRoom', { roomId });
      console.log(`Mediasoup room created/exists: ${roomId}`);
    } catch (err: any) {
      console.error('Failed to create Mediasoup room:', err);
      throw err;
    }
  }

  /**
   * Join a room in Mediasoup - creates a WebRTC transport.
   */
  async joinRoom(roomId: string, participantId: string): Promise<MediasoupTransportOptions> {
    this.connectionStateSubject.next('connecting');

    try {
      const result = await this.callMediasoupApi('joinRoom', {
        roomId,
        participantId,
      });

      return result.transportOptions as MediasoupTransportOptions;
    } catch (err: any) {
      this.connectionStateSubject.next('error');
      this.errorSubject.next(`Failed to join Mediasoup room: ${err.message}`);
      throw err;
    }
  }

  /**
   * Get RTP capabilities from the Mediasoup router.
   */
  async getRtpCapabilities(roomId: string): Promise<any> {
    const result = await this.callMediasoupApi('getRtpCapabilities', { roomId });
    return result.rtpCapabilities;
  }

  /**
   * Start producing audio from the microphone to Mediasoup.
   * 
   * Uses mediasoup-client SDK for proper WebRTC handshake:
   * 1. Create Device and load with router RTP capabilities
   * 2. Create send transport with server transport options
   * 3. Handle transport 'connect' event → call transportConnect API
   * 4. Handle transport 'produce' event → call produceAudio API
   * 5. Call transport.produce({ track }) to start sending audio
   */
  async produceAudio(
    roomId: string,
    participantId: string,
    transportOptions: MediasoupTransportOptions
  ): Promise<ProducerInfo> {
    const stream = this.microphoneService.getStream();
    if (!stream) {
      throw new Error('No microphone stream available. Request microphone access first.');
    }

    const audioTrack = stream.getAudioTracks()[0];
    if (!audioTrack) {
      throw new Error('No audio track available in microphone stream');
    }

    try {
      // Step 1: Create Device and load with router RTP capabilities
      this.device = new Device();

      const routerRtpCapabilities = await this.getRtpCapabilities(roomId);
      await this.device.load({ routerRtpCapabilities });

      console.log('Mediasoup Device loaded with router RTP capabilities');

      // Step 2: Create send transport with server transport options
      this.sendTransport = this.device.createSendTransport({
        id: transportOptions.id,
        iceParameters: transportOptions.iceParameters,
        iceCandidates: transportOptions.iceCandidates,
        dtlsParameters: transportOptions.dtlsParameters,
      });

      // Step 3: Handle transport 'connect' event
      this.sendTransport.on(
        'connect',
        async (
          { dtlsParameters }: { dtlsParameters: DtlsParameters },
          callback: () => void,
          errback: (error: Error) => void
        ) => {
          try {
            await this.callMediasoupApi('transportConnect', {
              roomId,
              participantId,
              dtlsParameters,
            });
            callback();
          } catch (error: any) {
            console.error('Transport connect failed:', error);
            errback(error);
          }
        }
      );

      // Step 4: Handle transport 'produce' event
      this.sendTransport.on(
        'produce',
        async (
          { kind, rtpParameters }: { kind: MediaKind; rtpParameters: RtpParameters },
          callback: ({ id }: { id: string }) => void,
          errback: (error: Error) => void
        ) => {
          try {
            const result = await this.callMediasoupApi('produceAudio', {
              roomId,
              participantId,
              kind,
              rtpParameters,
            });
            // The server returns { id: producerId }
            callback({ id: result.id });
          } catch (error: any) {
            console.error('Transport produce failed:', error);
            errback(error);
          }
        }
      );

      // Handle transport connection state changes
      this.sendTransport.on(
        'connectionstatechange',
        (connectionState: ConnectionState) => {
          console.log('Send transport connection state:', connectionState);
          switch (connectionState) {
            case 'connected':
              this.connectionStateSubject.next('connected');
              break;
            case 'disconnected':
            case 'failed':
              this.connectionStateSubject.next('disconnected');
              break;
          }
        }
      );

      // Step 5: Produce audio track
      this.producer = await this.sendTransport.produce({
        track: audioTrack,
        appData: { roomId, participantId },
      });

      console.log('Audio producer created:', this.producer.id);

      this.producer.on('transportclose', () => {
        console.log('Producer transport closed');
        this.producer = null;
      });

      return { producerId: this.producer.id };
    } catch (err: any) {
      this.connectionStateSubject.next('error');
      this.errorSubject.next(`Failed to produce audio: ${err.message}`);
      throw err;
    }
  }

  /**
   * Send an ICE candidate to the Mediasoup server.
   */
  private async sendIceCandidate(
    roomId: string,
    participantId: string,
    candidate: any
  ): Promise<void> {
    try {
      await this.callMediasoupApi('addIceCandidate', {
        roomId,
        participantId,
        candidate,
      });
    } catch (err) {
      console.error('Failed to send ICE candidate:', err);
    }
  }

  /**
   * Consume audio from another participant.
   */
  async consumeAudio(
    roomId: string,
    participantId: string,
    rtpCapabilities: any
  ): Promise<ConsumerInfo | null> {
    if (!this.device) {
      throw new Error('No device created. Call produceAudio first.');
    }

    try {
      const result = await this.callMediasoupApi('consumeAudio', {
        roomId,
        participantId,
        rtpCapabilities,
      });

      return result as ConsumerInfo;
    } catch (err: any) {
      console.error('Failed to consume audio:', err);
      return null;
    }
  }

  /**
   * Leave the Mediasoup room and clean up.
   */
  async leaveRoom(roomId: string, participantId: string): Promise<void> {
    try {
      await this.callMediasoupApi('leaveRoom', {
        roomId,
        participantId,
      });
    } catch (err) {
      console.error('Failed to leave Mediasoup room:', err);
    }

    this.cleanup();
  }

  /**
   * Clean up WebRTC resources.
   */
  private cleanup(): void {
    if (this.producer) {
      this.producer.close();
      this.producer = null;
    }

    if (this.sendTransport) {
      this.sendTransport.close();
      this.sendTransport = null;
    }

    this.device = null;
    this.connectionStateSubject.next('disconnected');
  }

  /**
   * Destroy the service.
   */
  destroy(): void {
    this.cleanup();
    this.connectionStateSubject.complete();
    this.errorSubject.complete();
  }
}

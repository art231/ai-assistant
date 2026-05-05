"""
WhisperLiveKit - Real-time speech transcription service.
Listens to RabbitMQ for audio chunks, transcribes them using Whisper,
and publishes transcripts back to RabbitMQ.
"""

import json
import base64
import logging
import time
import threading
from typing import Optional

import pika
import numpy as np

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger('whisper-livekit')

# ─── Configuration ───────────────────────────────────────────────
RABBITMQ_HOST = 'rabbitmq'
RABBITMQ_PORT = 5672
RABBITMQ_USER = 'guest'
RABBITMQ_PASS = 'guest'
AUDIO_CHUNKS_QUEUE = 'audio_chunks'
TRANSCRIPTS_QUEUE = 'transcripts'
WHISPER_MODEL = 'base'  # 'tiny', 'base', 'small', 'medium', 'large'
SAMPLE_RATE = 16000

# ─── Audio Buffer ────────────────────────────────────────────────
class AudioBuffer:
    """Buffers audio chunks per room/participant for transcription."""

    def __init__(self, max_duration_seconds: float = 5.0):
        self.buffers: dict[str, list[bytes]] = {}
        self.max_samples = int(SAMPLE_RATE * max_duration_seconds)

    def add_chunk(self, key: str, data: bytes):
        if key not in self.buffers:
            self.buffers[key] = []
        self.buffers[key].append(data)

    def get_and_clear(self, key: str) -> Optional[np.ndarray]:
        if key not in self.buffers or not self.buffers[key]:
            return None

        raw_data = b''.join(self.buffers[key])
        self.buffers[key] = []

        # Decode base64 and convert to float32 numpy array
        try:
            audio_bytes = base64.b64decode(raw_data)
            audio_array = np.frombuffer(audio_bytes, dtype=np.float32)
            return audio_array
        except Exception as e:
            logger.error(f"Failed to decode audio: {e}")
            return None

    def get_buffer_duration(self, key: str) -> float:
        if key not in self.buffers or not self.buffers[key]:
            return 0.0

        total_bytes = sum(len(chunk) for chunk in self.buffers[key])
        # Rough estimate: 4 bytes per float32 sample at 16kHz
        return total_bytes / (4 * SAMPLE_RATE)


# ─── Transcriber ─────────────────────────────────────────────────
class WhisperTranscriber:
    """Wrapper around Whisper model for transcription."""

    def __init__(self, model_size: str = 'base'):
        self.model_size = model_size
        self.model = None
        self.available = False

    def load_model(self):
        """Load the Whisper model (lazy initialization)."""
        try:
            import whisper
            logger.info(f"Loading Whisper model '{self.model_size}'...")
            self.model = whisper.load_model(self.model_size)
            self.available = True
            logger.info("Whisper model loaded successfully")
        except ImportError:
            logger.warning(
                "openai-whisper not installed. Using mock transcription."
            )
            self.available = False
        except Exception as e:
            logger.error(f"Failed to load Whisper model: {e}")
            self.available = False

    def transcribe(self, audio: np.ndarray) -> dict:
        """Transcribe audio and return result with text and language."""
        if not self.available or self.model is None:
            # Mock transcription for development
            return {
                'text': '[transcription placeholder]',
                'language': 'en',
                'segments': []
            }

        try:
            result = self.model.transcribe(
                audio,
                language=None,  # Auto-detect
                task='transcribe',
                fp16=False,  # Use FP32 for CPU compatibility
            )
            return result
        except Exception as e:
            logger.error(f"Transcription error: {e}")
            return {'text': '', 'language': 'en', 'segments': []}


# ─── RabbitMQ Handler ────────────────────────────────────────────
class RabbitMQHandler:
    """Handles RabbitMQ connection and message processing."""

    def __init__(self, transcriber: WhisperTranscriber):
        self.transcriber = transcriber
        self.audio_buffer = AudioBuffer(max_duration_seconds=5.0)
        self.connection: Optional[pika.BlockingConnection] = None
        self.channel: Optional[pika.channel.Channel] = None
        self.should_stop = False

    def connect(self):
        """Establish connection to RabbitMQ."""
        credentials = pika.PlainCredentials(RABBITMQ_USER, RABBITMQ_PASS)
        parameters = pika.ConnectionParameters(
            host=RABBITMQ_HOST,
            port=RABBITMQ_PORT,
            credentials=credentials,
            heartbeat=600,
            blocked_connection_timeout=300,
        )

        self.connection = pika.BlockingConnection(parameters)
        self.channel = self.connection.channel()

        # Declare queues
        self.channel.queue_declare(queue=AUDIO_CHUNKS_QUEUE, durable=True)
        self.channel.queue_declare(queue=TRANSCRIPTS_QUEUE, durable=True)

        # QoS: process one message at a time
        self.channel.basic_qos(prefetch_count=1)

        logger.info("Connected to RabbitMQ")

    def process_audio_chunk(self, ch, method, properties, body):
        """Process an incoming audio chunk from the queue."""
        try:
            message = json.loads(body)
            room_id = message.get('roomId')
            participant_id = message.get('participantId')
            data = message.get('data', '')
            timestamp = message.get('timestamp', 0)

            if not room_id or not participant_id:
                logger.warning("Invalid message: missing roomId or participantId")
                ch.basic_ack(delivery_tag=method.delivery_tag)
                return

            # Create buffer key
            buffer_key = f"{room_id}:{participant_id}"

            # Add chunk to buffer
            self.audio_buffer.add_chunk(buffer_key, data.encode('utf-8'))

            # Check if we have enough audio to transcribe (every ~3 seconds)
            buffer_duration = self.audio_buffer.get_buffer_duration(buffer_key)
            if buffer_duration >= 3.0:
                audio = self.audio_buffer.get_and_clear(buffer_key)
                if audio is not None and len(audio) > 0:
                    # Transcribe
                    result = self.transcriber.transcribe(audio)
                    text = result.get('text', '').strip()
                    language = result.get('language', 'en')

                    if text:
                        # Publish transcript
                        transcript_message = json.dumps({
                            'roomId': room_id,
                            'participantId': participant_id,
                            'userName': f'user_{participant_id[:8]}',
                            'text': text,
                            'isFinal': True,
                            'language': language,
                            'timestamp': timestamp,
                        })

                        self.channel.basic_publish(
                            exchange='',
                            routing_key=TRANSCRIPTS_QUEUE,
                            body=transcript_message.encode('utf-8'),
                            properties=pika.BasicProperties(
                                delivery_mode=2,  # Persistent
                            ),
                        )

                        logger.info(
                            f"Transcribed [{room_id}:{participant_id[:8]}]: "
                            f"{text[:60]}..."
                        )

            ch.basic_ack(delivery_tag=method.delivery_tag)

        except json.JSONDecodeError as e:
            logger.error(f"JSON decode error: {e}")
            ch.basic_ack(delivery_tag=method.delivery_tag)
        except Exception as e:
            logger.error(f"Error processing audio chunk: {e}")
            ch.basic_nack(delivery_tag=method.delivery_tag, requeue=False)

    def start_consuming(self):
        """Start consuming messages from the audio chunks queue."""
        self.channel.basic_consume(
            queue=AUDIO_CHUNKS_QUEUE,
            on_message_callback=self.process_audio_chunk,
        )

        logger.info("Waiting for audio chunks. To exit press CTRL+C")
        try:
            self.channel.start_consuming()
        except KeyboardInterrupt:
            self.should_stop = True
            self.channel.stop_consuming()

    def close(self):
        """Close the RabbitMQ connection."""
        if self.connection and not self.connection.is_closed:
            self.connection.close()
            logger.info("RabbitMQ connection closed")


# ─── Main ────────────────────────────────────────────────────────
def main():
    logger.info("Starting WhisperLiveKit transcription service...")

    # Initialize transcriber
    transcriber = WhisperTranscriber(model_size=WHISPER_MODEL)
    transcriber.load_model()

    # Initialize RabbitMQ handler
    handler = RabbitMQHandler(transcriber)

    # Connect with retry
    max_retries = 10
    retry_delay = 5

    for attempt in range(max_retries):
        try:
            handler.connect()
            break
        except Exception as e:
            logger.warning(
                f"Connection attempt {attempt + 1}/{max_retries} failed: {e}"
            )
            if attempt < max_retries - 1:
                time.sleep(retry_delay)
            else:
                logger.error("Max retries reached. Exiting.")
                return

    # Start consuming
    try:
        handler.start_consuming()
    except KeyboardInterrupt:
        logger.info("Shutting down...")
    finally:
        handler.close()


if __name__ == '__main__':
    main()

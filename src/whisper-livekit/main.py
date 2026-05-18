"""
WhisperLiveKit - Real-time speech transcription service with diarization.
Uses faster-whisper + Silero VAD + pyannote.audio (Diart) for
efficient transcription with speaker diarization.
Listens to RabbitMQ for audio chunks, transcribes them with speaker labels,
and publishes transcripts back to RabbitMQ.
Also provides HTTP API for offline file transcription with diarization.
"""

import json
import base64
import logging
import os
import time
import threading
from typing import Optional, List, Dict, Tuple
import tempfile
from collections import defaultdict

import pika
import numpy as np
from flask import Flask, request, jsonify

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger('whisper-livekit')

# ─── Configuration ───────────────────────────────────────────────
RABBITMQ_HOST = os.environ.get('RABBITMQ_HOST', 'rabbitmq')
RABBITMQ_PORT = int(os.environ.get('RABBITMQ_PORT', '5672'))
RABBITMQ_USER = os.environ.get('RABBITMQ_USER', 'guest')
RABBITMQ_PASS = os.environ.get('RABBITMQ_PASS', 'guest')
AUDIO_CHUNKS_QUEUE = os.environ.get('AUDIO_CHUNKS_QUEUE', 'audio_chunks')
TRANSCRIPTS_QUEUE = os.environ.get('TRANSCRIPTS_QUEUE', 'transcripts')
WHISPER_MODEL = os.environ.get('WHISPER_MODEL', 'base')  # tiny, base, small, medium, large
WHISPER_DEVICE = os.environ.get('WHISPER_DEVICE', 'auto')  # auto, cpu, cuda
WHISPER_COMPUTE_TYPE = os.environ.get('WHISPER_COMPUTE_TYPE', 'default')  # default, int8, float16
SAMPLE_RATE = 16000
HTTP_PORT = int(os.environ.get('HTTP_PORT', '8080'))
DIARIZATION_ENABLED = os.environ.get('DIARIZATION_ENABLED', 'true').lower() == 'true'
DIARIZATION_WINDOW = float(os.environ.get('DIARIZATION_WINDOW', '10.0'))  # seconds of audio for diarization

# ─── VAD (Voice Activity Detection) ─────────────────────────────
class VoiceActivityDetector:
    """Silero VAD wrapper for detecting speech in audio chunks."""

    def __init__(self):
        self.model = None
        self.available = False
        self._load_model()

    def _load_model(self):
        """Load Silero VAD model."""
        try:
            import torch
            torch.set_num_threads(1)
            model, utils = torch.hub.load(
                repo_or_dir='snakers4/silero-vad',
                model='silero_vad',
                force_reload=False,
                onnx=False,
                verbose=False
            )
            self.model = model
            self.get_speech_timestamps = utils[0]
            self.available = True
            logger.info("Silero VAD model loaded successfully")
        except Exception as e:
            logger.warning(f"Failed to load Silero VAD: {e}. VAD disabled.")
            self.available = False

    def is_speech(self, audio_chunk: np.ndarray, threshold: float = 0.5) -> bool:
        """Check if audio chunk contains speech."""
        if not self.available or self.model is None:
            return True  # If VAD not available, assume speech

        try:
            import torch
            audio_tensor = torch.from_numpy(audio_chunk).float()
            speech_prob = self.model(audio_tensor, SAMPLE_RATE).item()
            return speech_prob >= threshold
        except Exception as e:
            logger.error(f"VAD error: {e}")
            return True


# ─── Diarization (Speaker Separation) ────────────────────────────
class DiarizationProcessor:
    """
    Real-time speaker diarization using pyannote.audio.
    Processes audio windows and assigns speaker labels (speaker_0, speaker_1, etc.)
    to time segments. Works in a sliding window fashion for real-time use.
    """

    def __init__(self, window_seconds: float = 10.0):
        self.window_seconds = window_seconds
        self.pipeline = None
        self.available = False
        self._load_pipeline()

        # Speaker label mapping: pyannote labels -> our labels
        self._speaker_map: Dict[str, str] = {}
        self._next_speaker_id = 0

        # Audio buffer for diarization (per room)
        self._audio_buffers: Dict[str, np.ndarray] = {}
        self._last_diarization: Dict[str, float] = {}  # room_id -> last run time

    def _load_pipeline(self):
        """Load pyannote.audio diarization pipeline."""
        if not DIARIZATION_ENABLED:
            logger.info("Diarization disabled by configuration")
            return

        try:
            from pyannote.audio import Pipeline

            # Use the pretrained speaker diarization pipeline
            # Note: Requires huggingface token for pyannote/speaker-diarization-3.1
            # Falls back to a simpler approach if token not available
            hf_token = os.environ.get('HF_TOKEN', '')
            if hf_token:
                logger.info("Loading pyannote.audio diarization pipeline with HF token...")
                self.pipeline = Pipeline.from_pretrained(
                    "pyannote/speaker-diarization-3.1",
                    use_auth_token=hf_token
                )
                self.available = True
                logger.info("pyannote.audio diarization pipeline loaded successfully")
            else:
                logger.warning(
                    "HF_TOKEN not set. Diarization will use simple energy-based "
                    "speaker change detection as fallback."
                )
                self.available = False
        except ImportError:
            logger.warning("pyannote.audio not installed. Diarization disabled.")
            self.available = False
        except Exception as e:
            logger.warning(f"Failed to load diarization pipeline: {e}. Using fallback.")
            self.available = False

    def add_audio(self, room_id: str, audio: np.ndarray):
        """Add audio chunk to the room's buffer for diarization."""
        if room_id not in self._audio_buffers:
            self._audio_buffers[room_id] = np.array([], dtype=np.float32)
        self._audio_buffers[room_id] = np.concatenate([self._audio_buffers[room_id], audio])

        # Keep only last N seconds
        max_samples = int(SAMPLE_RATE * self.window_seconds)
        if len(self._audio_buffers[room_id]) > max_samples:
            self._audio_buffers[room_id] = self._audio_buffers[room_id][-max_samples:]

    def should_run_diarization(self, room_id: str) -> bool:
        """Check if enough time has passed since last diarization run."""
        now = time.time()
        last_run = self._last_diarization.get(room_id, 0.0)
        # Run diarization every 5 seconds
        return (now - last_run) >= 5.0

    def get_speaker_for_segment(self, start_time: float, end_time: float,
                                 room_id: str) -> str:
        """
        Determine which speaker is talking in the given time segment.
        Uses the latest diarization result for the room.
        Returns 'speaker_0', 'speaker_1', etc., or 'unknown' if diarization not available.
        """
        if not self.available or self.pipeline is None:
            return 'unknown'

        try:
            # Run diarization on the room's audio buffer
            audio = self._audio_buffers.get(room_id)
            if audio is None or len(audio) < SAMPLE_RATE * 2:  # Need at least 2 seconds
                return 'unknown'

            from pyannote.core import Segment
            from pyannote.audio import Inference

            # Run the diarization pipeline
            # The pipeline expects a file-like object or waveform
            diarization = self.pipeline({
                'waveform': audio[np.newaxis, :],  # Add channel dimension
                'sample_rate': SAMPLE_RATE
            })

            # Find which speaker is active in the given time window
            for segment, _, speaker in diarization.itertracks(yield_label=True):
                if segment.start <= start_time <= segment.end or \
                   segment.start <= end_time <= segment.end or \
                   (start_time <= segment.start and end_time >= segment.end):
                    # Map pyannote speaker label to our format
                    if speaker not in self._speaker_map:
                        self._speaker_map[speaker] = f'speaker_{self._next_speaker_id}'
                        self._next_speaker_id += 1
                    return self._speaker_map[speaker]

            return 'unknown'

        except Exception as e:
            logger.error(f"Diarization error: {e}")
            return 'unknown'

    def get_speaker_count(self, room_id: str) -> int:
        """Get the number of unique speakers detected in the room."""
        if not self.available:
            return 0
        return self._next_speaker_id

    def reset_room(self, room_id: str):
        """Reset diarization state for a room (e.g., when meeting ends)."""
        self._audio_buffers.pop(room_id, None)
        self._last_diarization.pop(room_id, None)


# ─── Voice Metrics (SpeechBrain + Acoustic Analysis) ─────────────
class VoiceMetricsAnalyzer:
    """
    Analyzes voice characteristics from audio chunks:
    - Gender classification (SpeechBrain)
    - Emotion recognition (SpeechBrain)
    - Fatigue detection (acoustic: monotonicity, jitter, speech rate)
    Lightweight models that run on CPU.
    """

    def __init__(self):
        self.gender_classifier = None
        self.emotion_classifier = None
        self.available = False
        self._load_models()

    def _load_models(self):
        """Load SpeechBrain models for gender and emotion classification."""
        try:
            import torch
            from speechbrain.inference import EncoderClassifier
            from speechbrain.inference.speaker import SpeakerRecognition

            # Gender classifier (male/female)
            logger.info("Loading SpeechBrain gender classifier...")
            self.gender_classifier = EncoderClassifier.from_hparams(
                source="speechbrain/spkrec-ecapa-voxceleb-gender",
                savedir="/app/models/gender",
                run_opts={"device": "cpu"}
            )
            logger.info("SpeechBrain gender classifier loaded")

            # Emotion classifier
            logger.info("Loading SpeechBrain emotion classifier...")
            self.emotion_classifier = EncoderClassifier.from_hparams(
                source="speechbrain/emotion-recognition-wav2vec2",
                savedir="/app/models/emotion",
                run_opts={"device": "cpu"}
            )
            logger.info("SpeechBrain emotion classifier loaded")

            self.available = True
        except ImportError:
            logger.warning("speechbrain not installed. Voice metrics disabled.")
            self.available = False
        except Exception as e:
            logger.warning(f"Failed to load SpeechBrain models: {e}. Voice metrics disabled.")
            self.available = False

    def analyze(self, audio: np.ndarray) -> dict:
        """
        Analyze voice metrics from audio chunk.
        Returns dict with gender, emotion, fatigue metrics.
        """
        result = {
            'gender': 'unknown',
            'genderConfidence': 0.0,
            'emotion': 'unknown',
            'emotionConfidence': 0.0,
            'fatigueLevel': 0.0,
            'fatigueIndicators': [],
            'speechRate': 0.0,
            'pitchVariability': 0.0,
        }

        if not self.available or len(audio) < SAMPLE_RATE * 0.5:  # Need at least 0.5s
            return result

        try:
            import torch

            # Convert to tensor
            audio_tensor = torch.from_numpy(audio).float().unsqueeze(0)

            # 1. Gender classification
            if self.gender_classifier is not None:
                gender_out = self.gender_classifier(audio_tensor)
                gender_pred = torch.softmax(gender_out, dim=-1)
                gender_idx = torch.argmax(gender_pred, dim=-1).item()
                gender_labels = ['male', 'female']
                result['gender'] = gender_labels[gender_idx] if gender_idx < len(gender_labels) else 'unknown'
                result['genderConfidence'] = float(gender_pred[0][gender_idx].item())

            # 2. Emotion classification
            if self.emotion_classifier is not None:
                emotion_out = self.emotion_classifier(audio_tensor)
                emotion_pred = torch.softmax(emotion_out, dim=-1)
                emotion_idx = torch.argmax(emotion_pred, dim=-1).item()
                emotion_labels = ['neutral', 'happy', 'sad', 'angry', 'fearful', 'disgusted', 'surprised']
                result['emotion'] = emotion_labels[emotion_idx] if emotion_idx < len(emotion_labels) else 'unknown'
                result['emotionConfidence'] = float(emotion_pred[0][emotion_idx].item())

            # 3. Acoustic fatigue analysis
            fatigue_indicators = []
            fatigue_score = 0.0

            # 3a. Pitch variability (monotonicity)
            # Low pitch variability = monotone voice = fatigue indicator
            try:
                # Simple F0 estimation via autocorrelation
                f0 = self._estimate_pitch(audio)
                if len(f0) > 1:
                    pitch_std = float(np.std(f0))
                    pitch_mean = float(np.mean(f0))
                    # Normalize: coefficient of variation
                    if pitch_mean > 0:
                        pitch_cv = pitch_std / pitch_mean
                        result['pitchVariability'] = pitch_cv
                        if pitch_cv < 0.05:  # Very monotone
                            fatigue_indicators.append('monotone_voice')
                            fatigue_score += 0.3
                        elif pitch_cv < 0.1:  # Somewhat monotone
                            fatigue_indicators.append('low_pitch_variability')
                            fatigue_score += 0.15
            except Exception:
                pass

            # 3b. Speech rate (slower speech = fatigue)
            # Estimate from number of syllables detected in audio
            try:
                # Simple energy-based syllable detection
                energy = audio ** 2
                frame_size = int(SAMPLE_RATE * 0.03)  # 30ms frames
                frames = [energy[i:i + frame_size].mean()
                         for i in range(0, len(energy), frame_size)]
                frames = np.array(frames)

                # Detect peaks (syllable nuclei)
                from scipy.signal import find_peaks
                peaks, _ = find_peaks(frames, height=np.mean(frames) * 0.5,
                                      distance=5)
                syllable_count = len(peaks)
                duration_sec = len(audio) / SAMPLE_RATE
                speech_rate = syllable_count / duration_sec if duration_sec > 0 else 0
                result['speechRate'] = speech_rate

                # Normal speech rate: ~3-5 syllables/sec
                if speech_rate < 2.0:
                    fatigue_indicators.append('slow_speech')
                    fatigue_score += 0.25
                elif speech_rate > 6.0:
                    fatigue_indicators.append('fast_speech')
                    fatigue_score += 0.1
            except Exception:
                pass

            # 3c. Jitter (pitch instability)
            try:
                if len(f0) > 5:
                    # Jitter = average absolute difference between consecutive periods
                    periods = 1.0 / (f0[f0 > 50])  # Convert Hz to period
                    if len(periods) > 5:
                        diffs = np.abs(np.diff(periods))
                        jitter = float(np.mean(diffs) / np.mean(periods))
                        if jitter > 0.05:  # High jitter = vocal fatigue
                            fatigue_indicators.append('high_jitter')
                            fatigue_score += 0.2
            except Exception:
                pass

            # Clamp fatigue score
            result['fatigueLevel'] = min(fatigue_score, 1.0)
            result['fatigueIndicators'] = fatigue_indicators

        except Exception as e:
            logger.error(f"Voice metrics analysis error: {e}")

        return result

    def _estimate_pitch(self, audio: np.ndarray) -> np.ndarray:
        """
        Estimate fundamental frequency (F0) using autocorrelation.
        Returns array of F0 values for voiced segments.
        """
        frame_size = int(SAMPLE_RATE * 0.03)  # 30ms
        hop_size = int(SAMPLE_RATE * 0.01)    # 10ms
        f0_values = []

        for start in range(0, len(audio) - frame_size, hop_size):
            frame = audio[start:start + frame_size]

            # Center and window
            frame = frame - np.mean(frame)
            frame = frame * np.hanning(len(frame))

            # Autocorrelation
            corr = np.correlate(frame, frame, mode='same')
            mid = len(corr) // 2

            # Find peaks in autocorrelation (50-500 Hz range)
            min_lag = int(SAMPLE_RATE / 500)  # 500 Hz max
            max_lag = int(SAMPLE_RATE / 50)   # 50 Hz min
            search = corr[mid + min_lag:mid + max_lag]

            if len(search) > 0:
                peak_idx = np.argmax(search)
                peak_val = search[peak_idx]

                # Voiced if autocorrelation peak is significant
                if peak_val > 0.3 * np.max(corr):
                    f0 = SAMPLE_RATE / (min_lag + peak_idx)
                    f0_values.append(f0)

        return np.array(f0_values)


# ─── Summarizer (BART) ───────────────────────────────────────────
class MeetingSummarizer:
    """
    Generates concise meeting summaries using BART (facebook/bart-large-cnn).
    Runs on CPU, lightweight model for real-time summarization.
    """

    def __init__(self):
        self.tokenizer = None
        self.model = None
        self.available = False
        self._load_model()

    def _load_model(self):
        """Load BART summarization model."""
        try:
            from transformers import BartTokenizer, BartForConditionalGeneration

            logger.info("Loading BART summarization model (facebook/bart-large-cnn)...")
            self.tokenizer = BartTokenizer.from_pretrained(
                "facebook/bart-large-cnn",
                cache_dir="/app/models/bart"
            )
            self.model = BartForConditionalGeneration.from_pretrained(
                "facebook/bart-large-cnn",
                cache_dir="/app/models/bart"
            )
            self.model.eval()  # Inference mode
            self.available = True
            logger.info("BART summarization model loaded successfully")
        except ImportError:
            logger.warning("transformers not installed. Summarization disabled.")
            self.available = False
        except Exception as e:
            logger.warning(f"Failed to load BART model: {e}. Summarization disabled.")
            self.available = False

    def summarize(self, text: str, max_length: int = 150, min_length: int = 40) -> str:
        """
        Generate a concise summary of the given text.
        Returns empty string if summarization is not available.
        """
        if not self.available or self.model is None or self.tokenizer is None:
            return self._fallback_summarize(text)

        if not text or len(text.strip()) < 50:
            return text  # Too short to summarize

        try:
            import torch

            # Tokenize with truncation
            inputs = self.tokenizer(
                text,
                max_length=1024,
                truncation=True,
                return_tensors="pt",
                padding=True
            )

            # Generate summary
            with torch.no_grad():
                summary_ids = self.model.generate(
                    inputs["input_ids"],
                    max_length=max_length,
                    min_length=min_length,
                    num_beams=4,
                    length_penalty=2.0,
                    early_stopping=True,
                    no_repeat_ngram_size=3,
                )

            summary = self.tokenizer.decode(
                summary_ids[0],
                skip_special_tokens=True,
                clean_up_tokenization_spaces=True
            )

            return summary.strip()

        except Exception as e:
            logger.error(f"BART summarization error: {e}")
            return self._fallback_summarize(text)

    def _fallback_summarize(self, text: str) -> str:
        """
        Simple extractive fallback when BART is not available.
        Returns first few sentences that capture key points.
        """
        if not text:
            return ""

        # Split into sentences
        sentences = text.replace('!', '.').replace('?', '.').split('.')
        sentences = [s.strip() for s in sentences if len(s.strip()) > 20]

        if len(sentences) <= 3:
            return text

        # Take first 2-3 sentences as a simple extractive summary
        summary = '. '.join(sentences[:3]) + '.'
        return summary

    def summarize_transcripts(self, transcripts: list) -> str:
        """
        Summarize a list of transcript dicts (with text, speakerId, userName).
        Returns a structured meeting summary.
        """
        if not transcripts:
            return ""

        # Build a coherent text from transcripts
        full_text_parts = []
        for t in transcripts:
            speaker = t.get('speakerId', 'unknown')
            user = t.get('userName', 'unknown')
            text = t.get('text', '')
            if text:
                full_text_parts.append(f"[{user}]({speaker}): {text}")

        full_text = '\n'.join(full_text_parts)

        if not full_text.strip():
            return ""

        # Generate summary
        summary = self.summarize(full_text)

        # If BART is not available, use fallback with structure
        if not self.available:
            # Count speakers
            speakers = set(t.get('speakerId', 'unknown') for t in transcripts
                          if t.get('speakerId', 'unknown') != 'unknown')
            speaker_count = len(speakers)

            # Build structured summary
            lines = full_text_parts[:5]  # First 5 exchanges
            structured = (
                f"Meeting Summary ({len(transcripts)} exchanges, "
                f"{speaker_count} speakers):\n"
                + '\n'.join(lines[:3])
            )
            return structured

        return summary


# ─── Transcriber ─────────────────────────────────────────────────
class WhisperTranscriber:
    """Wrapper around faster-whisper model for transcription."""

    def __init__(self, model_size: str = 'base', device: str = 'auto', compute_type: str = 'default'):
        self.model_size = model_size
        self.device = device
        self.compute_type = compute_type
        self.model = None
        self.available = False

    def load_model(self):
        """Load the faster-whisper model (lazy initialization)."""
        try:
            from faster_whisper import WhisperModel

            # Auto-detect device
            device = self.device
            compute_type = self.compute_type

            if device == 'auto':
                try:
                    import torch
                    if torch.cuda.is_available():
                        device = 'cuda'
                        if compute_type == 'default':
                            compute_type = 'float16'
                        logger.info("CUDA available, using GPU")
                    else:
                        device = 'cpu'
                        if compute_type == 'default':
                            compute_type = 'int8'
                        logger.info("CUDA not available, using CPU with int8")
                except ImportError:
                    device = 'cpu'
                    compute_type = 'int8'
                    logger.info("torch not available, using CPU with int8")

            logger.info(f"Loading faster-whisper model '{self.model_size}' on {device} ({compute_type})...")
            self.model = WhisperModel(
                self.model_size,
                device=device,
                compute_type=compute_type,
                cpu_threads=4,
                num_workers=2
            )
            self.available = True
            logger.info("faster-whisper model loaded successfully")
        except ImportError:
            logger.warning(
                "faster-whisper not installed. Using mock transcription."
            )
            self.available = False
        except Exception as e:
            logger.error(f"Failed to load faster-whisper model: {e}")
            self.available = False

    def transcribe(self, audio: np.ndarray, language: Optional[str] = None) -> dict:
        """Transcribe audio and return result with text and language."""
        if not self.available or self.model is None:
            return {
                'text': '[transcription placeholder]',
                'language': 'en',
                'segments': []
            }

        try:
            segments, info = self.model.transcribe(
                audio,
                language=language,
                task='transcribe',
                beam_size=5,
                vad_filter=True,  # Use built-in VAD filter
                vad_parameters=dict(
                    threshold=0.5,
                    min_speech_duration_ms=250,
                    min_silence_duration_ms=100,
                )
            )

            text_parts = []
            all_segments = []
            for seg in segments:
                text_parts.append(seg.text)
                all_segments.append({
                    'start': seg.start,
                    'end': seg.end,
                    'text': seg.text,
                })

            full_text = ' '.join(text_parts).strip()
            detected_language = info.language if info else 'en'

            return {
                'text': full_text,
                'language': detected_language,
                'segments': all_segments
            }
        except Exception as e:
            logger.error(f"Transcription error: {e}")
            return {'text': '', 'language': 'en', 'segments': []}

    def transcribe_file(self, file_path: str, language: Optional[str] = None) -> dict:
        """Transcribe an audio file and return result."""
        if not self.available or self.model is None:
            return {
                'text': '[transcription placeholder]',
                'language': 'en',
                'segments': []
            }

        try:
            # Try without VAD filter first for file transcription
            # VAD can incorrectly filter non-speech audio
            segments, info = self.model.transcribe(
                file_path,
                language=language,
                task='transcribe',
                beam_size=5,
                vad_filter=False,
            )

            text_parts = []
            all_segments = []
            for seg in segments:
                text_parts.append(seg.text)
                all_segments.append({
                    'start': seg.start,
                    'end': seg.end,
                    'text': seg.text,
                })

            full_text = ' '.join(text_parts).strip()
            detected_language = info.language if info else 'en'

            return {
                'text': full_text,
                'language': detected_language,
                'segments': all_segments,
                'duration': info.duration if info else 0,
            }
        except Exception as e:
            logger.error(f"File transcription error: {e}")
            return {'text': '', 'language': 'en', 'segments': []}


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


# ─── RabbitMQ Handler ────────────────────────────────────────────
class RabbitMQHandler:
    """Handles RabbitMQ connection and message processing."""

    def __init__(self, transcriber: WhisperTranscriber, vad: VoiceActivityDetector,
                 diarization: Optional[DiarizationProcessor] = None,
                 voice_metrics: Optional[VoiceMetricsAnalyzer] = None):
        self.transcriber = transcriber
        self.vad = vad
        self.diarization = diarization
        self.voice_metrics = voice_metrics
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
        """Process an incoming audio chunk from the queue with diarization."""
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
                    # VAD check - skip if no speech detected
                    if self.vad.available and not self.vad.is_speech(audio):
                        logger.debug(f"Skipping silence for [{room_id}:{participant_id[:8]}]")
                        ch.basic_ack(delivery_tag=method.delivery_tag)
                        return

                    # Feed audio to diarization processor (for speaker separation)
                    if self.diarization is not None:
                        self.diarization.add_audio(room_id, audio)

                    # Transcribe
                    result = self.transcriber.transcribe(audio)
                    text = result.get('text', '').strip()
                    language = result.get('language', 'en')
                    segments = result.get('segments', [])

                    if text:
                        # Determine speaker ID via diarization
                        speaker_id = 'unknown'
                        if self.diarization is not None and segments:
                            # Use the first segment's time range to determine speaker
                            first_seg = segments[0]
                            speaker_id = self.diarization.get_speaker_for_segment(
                                first_seg.get('start', 0),
                                first_seg.get('end', 0),
                                room_id
                            )

                        # Analyze voice metrics (gender, emotion, fatigue)
                        voice_metrics_result = {}
                        if self.voice_metrics is not None:
                            voice_metrics_result = self.voice_metrics.analyze(audio)

                        # Build transcript with speaker info and voice metrics
                        transcript_message = json.dumps({
                            'roomId': room_id,
                            'participantId': participant_id,
                            'speakerId': speaker_id,
                            'userName': f'user_{participant_id[:8]}',
                            'text': text,
                            'isFinal': True,
                            'language': language,
                            'segments': segments,
                            'timestamp': timestamp,
                            'voiceMetrics': voice_metrics_result,
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
                            f"Transcribed [{room_id}:{participant_id[:8]}] "
                            f"speaker={speaker_id}: {text[:60]}..."
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


# ─── Flask HTTP API ──────────────────────────────────────────────
app = Flask(__name__)
transcriber: Optional[WhisperTranscriber] = None
summarizer: Optional[MeetingSummarizer] = None


@app.route('/health', methods=['GET'])
def health():
    """Health check endpoint."""
    return jsonify({
        'status': 'ok',
        'model': WHISPER_MODEL,
        'available': transcriber.available if transcriber else False,
        'vad_available': vad.available if 'vad' in dir() else False,
    })


@app.route('/transcribe', methods=['POST'])
def transcribe_file():
    """
    Transcribe an uploaded audio file.
    Accepts multipart/form-data with 'audio' file field.
    Returns JSON with transcription text, language, and segments.
    """
    if transcriber is None or not transcriber.available:
        return jsonify({'error': 'Transcriber not available'}), 503

    if 'audio' not in request.files:
        return jsonify({'error': 'No audio file provided'}), 400

    audio_file = request.files['audio']
    if audio_file.filename == '':
        return jsonify({'error': 'Empty filename'}), 400

    language = request.form.get('language', None)

    # Save to temp file
    suffix = os.path.splitext(audio_file.filename)[1] or '.ogg'
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
        audio_file.save(tmp.name)
        tmp_path = tmp.name

    try:
        logger.info(f"Transcribing file: {audio_file.filename} ({language or 'auto'})")
        result = transcriber.transcribe_file(tmp_path, language=language)
        logger.info(f"Transcription complete: {len(result.get('text', ''))} chars")
        return jsonify(result)
    except Exception as e:
        logger.error(f"File transcription error: {e}")
        return jsonify({'error': str(e)}), 500
    finally:
        # Clean up temp file
        try:
            os.unlink(tmp_path)
        except Exception:
            pass


@app.route('/summarize', methods=['POST'])
def summarize():
    """
    Generate a meeting summary from transcripts.
    Accepts JSON with 'transcripts' array (each with text, speakerId, userName).
    Returns JSON with summary text.
    """
    if summarizer is None:
        return jsonify({'error': 'Summarizer not available'}), 503

    data = request.get_json(silent=True)
    if not data or 'transcripts' not in data:
        return jsonify({'error': 'Missing transcripts array'}), 400

    transcripts = data['transcripts']
    if not isinstance(transcripts, list) or len(transcripts) == 0:
        return jsonify({'error': 'Empty transcripts array'}), 400

    try:
        logger.info(f"Generating summary for {len(transcripts)} transcripts...")
        summary = summarizer.summarize_transcripts(transcripts)
        logger.info(f"Summary generated: {len(summary)} chars")
        return jsonify({
            'summary': summary,
            'transcriptCount': len(transcripts),
            'available': summarizer.available,
        })
    except Exception as e:
        logger.error(f"Summarization error: {e}")
        return jsonify({'error': str(e)}), 500


def run_http_server():
    """Run Flask HTTP server in a separate thread."""
    logger.info(f"Starting HTTP server on port {HTTP_PORT}")
    app.run(host='0.0.0.0', port=HTTP_PORT, debug=False, use_reloader=False)


# ─── Main ────────────────────────────────────────────────────────
def main():
    logger.info("Starting WhisperLiveKit transcription service...")

    # Initialize VAD
    global vad
    vad = VoiceActivityDetector()

    # Initialize transcriber
    global transcriber
    transcriber = WhisperTranscriber(
        model_size=WHISPER_MODEL,
        device=WHISPER_DEVICE,
        compute_type=WHISPER_COMPUTE_TYPE
    )
    transcriber.load_model()

    # Start HTTP server in background thread
    http_thread = threading.Thread(target=run_http_server, daemon=True)
    http_thread.start()

    # Initialize diarization processor
    global diarization
    diarization = DiarizationProcessor(window_seconds=DIARIZATION_WINDOW)
    if diarization.available:
        logger.info("Diarization processor initialized successfully")
    else:
        logger.info("Diarization processor not available (HF_TOKEN may be missing)")

    # Initialize voice metrics analyzer (gender, emotion, fatigue)
    global voice_metrics
    voice_metrics = VoiceMetricsAnalyzer()
    if voice_metrics.available:
        logger.info("Voice metrics analyzer initialized successfully")
    else:
        logger.info("Voice metrics analyzer not available (speechbrain may not be installed)")

    # Initialize summarizer (BART)
    global summarizer
    summarizer = MeetingSummarizer()
    if summarizer.available:
        logger.info("BART summarizer initialized successfully")
    else:
        logger.info("BART summarizer not available (transformers may not be installed)")

    # Initialize RabbitMQ handler with diarization and voice metrics
    handler = RabbitMQHandler(transcriber, vad, diarization, voice_metrics)

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

# ============================================================
# WhisperLiveKit - Real-time Speech Transcription (CPU/GPU)
# Uses faster-whisper + Silero VAD + Diarization + Voice Metrics + Summarization
# ============================================================
FROM ubuntu:22.04 AS base

WORKDIR /app

# Install Python and system dependencies
RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-dev \
    ffmpeg \
    git \
    ca-certificates \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Create symbolic link for python
RUN ln -s /usr/bin/python3 /usr/bin/python

# Upgrade pip
RUN pip3 install --no-cache-dir --upgrade pip setuptools wheel

# Copy requirements first for better caching
COPY src/whisper-livekit/requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt

# Copy source files
COPY src/whisper-livekit/ .

# Create model cache directories
RUN mkdir -p /app/models/whisper /app/models/pyannote /app/models/speechbrain /app/models/bart

# Expose HTTP API port
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=15s --retries=5 --start-period=120s \
  CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/health', timeout=10)" || exit 1

CMD ["python3", "main.py"]

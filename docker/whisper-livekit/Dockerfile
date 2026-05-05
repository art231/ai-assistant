# ============================================================
# WhisperLiveKit - Real-time Speech Transcription
# ============================================================
FROM nvidia/cuda:12.4.0-runtime-ubuntu22.04

WORKDIR /app

# Install Python and dependencies
RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-dev \
    ffmpeg \
    git \
    && rm -rf /var/lib/apt/lists/*

# Create symbolic link for python
RUN ln -s /usr/bin/python3 /usr/bin/python

# Copy requirements first for better caching
COPY src/whisper-livekit/requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt

# Copy source files
COPY src/whisper-livekit/ .

# Expose health check port
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --retries=3 \
  CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/health')" || exit 1

CMD ["python3", "main.py"]

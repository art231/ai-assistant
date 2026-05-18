#!/usr/bin/env python3
"""
E2E Pipeline Test: Audio File -> Upload -> Whisper -> Ollama -> PDF Export
Tests the full pipeline using HTTP APIs (bypasses Mediasoup WebRTC).
"""
import requests
import json
import time
import sys
import os

BACKEND_URL = "http://backend:5000"

def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)

def main():
    # 1. Create a room via HTTP
    log("=== Step 1: Creating room ===")
    r = requests.post(f"{BACKEND_URL}/api/rooms",
                      json={"name": "E2E Pipeline Test", "maxParticipants": 10},
                      timeout=10)
    room = r.json()
    room_id = room["id"]
    log(f"Room created: {room_id} ({room['name']})")

    # 2. Upload audio file directly to backend
    log("=== Step 2: Uploading audio file ===")
    audio_path = "/tmp/test_mixed.wav"
    if not os.path.exists(audio_path):
        log(f"ERROR: Audio file not found: {audio_path}")
        sys.exit(1)

    file_size = os.path.getsize(audio_path)
    log(f"Audio file size: {file_size} bytes")

    with open(audio_path, 'rb') as f:
        files = {'file': ('test_mixed.wav', f, 'audio/wav')}
        r = requests.post(
            f"{BACKEND_URL}/api/recordings/{room_id}/upload-audio",
            files=files,
            timeout=30
        )

    log(f"Upload status: {r.status_code}")
    upload_result = r.json()
    log(f"Upload result: {json.dumps(upload_result, indent=2)[:300]}")

    recording_id = upload_result.get('id')
    if not recording_id:
        log("ERROR: No recording ID returned")
        sys.exit(1)

    log(f"Recording ID: {recording_id}")

    # 3. Wait for post-processing (Whisper + Ollama)
    log("=== Step 3: Waiting for post-processing (30s) ===")
    for i in range(30):
        time.sleep(5)
        r = requests.get(f"{BACKEND_URL}/api/recordings/{recording_id}/debug", timeout=10)
        debug = r.json()
        has_text = debug.get('hasFullText', False)
        has_summary = debug.get('hasSummary', False)
        log(f"  [{i*5+5}s] hasFullText={has_text}, hasSummary={has_summary}, "
            f"audioFileExists={debug.get('audioFileExists')}, "
            f"audioFileSize={debug.get('audioFileSizeBytes')}")

        if has_text and has_summary:
            log("Post-processing complete!")
            break

    # 4. Check final debug info
    log("=== Step 4: Final debug info ===")
    r = requests.get(f"{BACKEND_URL}/api/recordings/{recording_id}/debug", timeout=10)
    debug = r.json()
    log(f"Debug: {json.dumps(debug, indent=2)[:500]}")

    # 5. Export PDF
    log("=== Step 5: Exporting PDF ===")
    r = requests.get(f"{BACKEND_URL}/api/recordings/{recording_id}/export-pdf", timeout=30)
    if r.status_code == 200:
        pdf_path = f"/tmp/recording_{recording_id}.pdf"
        with open(pdf_path, 'wb') as f:
            f.write(r.content)
        log(f"PDF exported successfully: {pdf_path} ({len(r.content)} bytes)")
        log(f"PDF content type: {r.headers.get('Content-Type')}")
    else:
        log(f"PDF export failed: {r.status_code}")
        log(f"Response: {r.text[:500]}")

    # 6. List all recordings
    log("=== Step 6: All recordings ===")
    r = requests.get(f"{BACKEND_URL}/api/recordings", timeout=10)
    recordings = r.json()
    for rec in recordings:
        log(f"  {rec['id'][:8]}... room={rec['roomId'][:8]}... "
            f"status={rec.get('status')} size={rec.get('fileSizeBytes')}B "
            f"has_transcript={rec.get('transcript') is not None}")

    log("=== E2E Test Complete ===")

if __name__ == "__main__":
    main()

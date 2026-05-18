import numpy as np
from scipy.io import wavfile
import os

sample_rate = 16000
duration = 5
t = np.linspace(0, duration, int(sample_rate * duration), endpoint=False)
freq = 200
audio = 0.3 * np.sin(2 * np.pi * freq * t)
audio += 0.15 * np.sin(2 * np.pi * freq * 2 * t)
audio += 0.05 * np.sin(2 * np.pi * freq * 3 * t)
audio = audio / np.max(np.abs(audio)) * 0.8

output_path = '/tmp/test_speech.wav'
wavfile.write(output_path, sample_rate, (audio * 32767).astype(np.int16))
print(f'Created: {output_path}, size: {os.path.getsize(output_path)} bytes')

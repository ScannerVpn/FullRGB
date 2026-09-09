#!/usr/bin/env python3
"""Play a 440 Hz test tone on the default output for N seconds, then silence.
Uses PowerShell + System.Media.SoundPlayer on a generated WAV, or falls back to
a looping [console]::beep pattern. Writes a WAV to %TEMP% first."""
import math
import struct
import subprocess
import sys
import tempfile
import wave
import time
import os

SECONDS_TONE = 4
SECONDS_SILENCE = 2
SAMPLE_RATE = 44100
FREQ = 220  # low enough to feed the bass band

path = os.path.join(tempfile.gettempdir(), "fullrgb_tone.wav")
with wave.open(path, "w") as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(SAMPLE_RATE)
    frames = bytearray()
    for i in range(SAMPLE_RATE * SECONDS_TONE):
        v = int(22000 * math.sin(2 * math.pi * FREQ * i / SAMPLE_RATE))
        frames += struct.pack("<hh", v, v)
    for i in range(SAMPLE_RATE * SECONDS_SILENCE):
        frames += struct.pack("<hh", 0, 0)
    w.writeframes(bytes(frames))

ps = (
    "$p = New-Object System.Media.SoundPlayer '%s'; $p.PlaySync()" % path.replace("\\", "\\\\")
)
print("playing: 4s tone (220 Hz) then 2s silence", flush=True)
subprocess.run(["powershell.exe", "-NoProfile", "-Command", ps], check=False)
print("done", flush=True)

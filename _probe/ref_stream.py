import socket, struct, time, subprocess, os, threading, math

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(10)

# Use the ACTUAL openrgb-python client for the stream (it demonstrably works with iCUE-style apps)
from openrgb.orgb import OpenRGBClient
from openrgb.utils import RGBColor

client = OpenRGBClient("127.0.0.1", 6742, name="ref-stream")
print("connected, devices:", [d.name for d in client.devices])
dev = client.devices[0]
print(f"streaming to {dev.name} ({len(dev.leds)} leds)...")
t0 = time.time()
frames = 0
try:
    while time.time() - t0 < 4:
        tt = time.time() - t0
        r = int(127 + 127 * math.sin(tt * 3))
        g = int(127 + 127 * math.sin(tt * 3 + 2))
        b = int(127 + 127 * math.sin(tt * 3 + 4))
        dev.set_color(RGBColor(r, g, b), True)
        frames += 1
        time.sleep(0.033)
    print(f"REF CLIENT STREAM OK: {frames} frames @30fps")
except Exception as e:
    print(f"ref client stream FAILED after {frames}: {type(e).__name__} {e}")
time.sleep(1)
print("alive:", proc.poll() is None)
proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

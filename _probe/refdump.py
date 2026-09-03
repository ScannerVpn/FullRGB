import socket, struct, time, subprocess, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(10)

from openrgb.orgb import OpenRGBClient
c = OpenRGBClient("127.0.0.1", 6742)
for dev in c.devices:
    print(dev.name)
    for z in dev.zones:
        print("   zone:", z.name, "type:", z.type, "leds:", len(z.leds), "mat:", z.mat_height, "x", z.mat_width)
    print("   leds:", [l.name for l in dev.leds][:5], "... total", len(dev.leds))
    print("   modes:", [m.name for m in dev.modes])

import subprocess
subprocess.run(['taskkill', '/F', '/IM', 'OpenRGB.exe'], capture_output=True)

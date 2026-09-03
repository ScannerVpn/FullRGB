import subprocess, time, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(9)
assert proc.poll() is None, f"DIED rc={proc.returncode}"

try:
    from openrgb.orgb import OpenRGBClient
    from openrgb.utils import RGBColor
    print("module loaded, connecting...")
    client = OpenRGBClient("127.0.0.1", 6742)
    print(f"CONNECTED! devices: {len(client.devices)}")
    for d in client.devices:
        print(f"  - {d.name} | leds={len(d.leds)} zones={len(d.zones)} modes={len(d.modes)}")
    print("\nsetting solid red on all devices...")
    for d in client.devices:
        try:
            d.set_color(RGBColor(255, 0, 0), True)
            print(f"  red -> {d.name}")
        except Exception as e:
            print(f"  FAIL {d.name}: {e}")
    time.sleep(2)
    for d in client.devices:
        try: d.set_color(RGBColor(0, 0, 0), True)
        except Exception as e: print(f"  off FAIL {d.name}: {e}")
    print("color control via reference client: OK")
    time.sleep(2)
    print(f"server alive: {proc.poll() is None}")
finally:
    proc.kill()
    os.system('taskkill /F /IM OpenRGB.exe 2>nul')

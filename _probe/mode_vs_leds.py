import socket, struct, time, subprocess, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(10)

s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(3)

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        c = s.recv(n - len(buf))
        if not c: raise EOFError
        buf += c
    return buf

def send_pkt(dev, ptype, payload=b""):
    s.sendall(b"ORGB" + struct.pack("<II", dev, ptype) + struct.pack("<I", len(payload)) + payload)

def recv_pkt():
    hdr = recv_exact(16)
    dev, ptype = struct.unpack("<II", hdr[4:12])
    size = struct.unpack("<I", hdr[12:16])[0]
    return dev, ptype, recv_exact(size)

send_pkt(0, 40, struct.pack("<I", 4))
try: recv_pkt()
except socket.timeout: pass
send_pkt(0, 50, b"mode-vs-leds\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# Hypothesis: set_color sends UPDATE_MODE (switching mode) not UPDATE_LEDS!
# Check what openrgb-python's set_color actually sends: read its source path quickly
# Direct-mode path: DeviceData.set_color -> sends UPDATE_LEDS only if in direct mode,
# else sends UPDATE_MODE with per-led colors. The ASUS board 'Direct' mode has cmode=1 (PER_LED).
# After SET_CUSTOM_MODE, the active mode is Direct (mode 0). Then UPDATE_LEDS should work...
# BUT maybe the server requires UPDATE_MODE with the direct mode FIRST (mode data blob),
# or it requires that UPDATE_LEDS comes AFTER the mode has per-led colors enabled.
# Test A: UPDATE_MODE with mode index 0 (Direct) BEFORE streaming:
def pack_mode0():
    # minimal mode blob for 'Direct' idx0: name,value,flags,smin,smax,bmin,bmax,cmin,cmax,speed,bval,dir,cmode,ncolors,colors
    name = struct.pack("<H", 7) + b"Direct\x00"
    body = name + struct.pack("<iiIIIIIIIIII", 255, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0) + struct.pack("<H", 0)
    return struct.pack("<I", len(body) + 4) + body

send_pkt(0, 1053, struct.pack("<I", 0) + pack_mode0())  # UPDATE_MODE? (1101?) - rc numbering!

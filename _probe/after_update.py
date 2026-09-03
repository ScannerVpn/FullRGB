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
send_pkt(0, 50, b"cmd-test\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# isolate: what command after a dev0 UPDATE_LEDS kills the connection?
# A: REQUEST_CONTROLLER_COUNT (same as openrgb-python does after EVERY command? No.)
# openrgb-python's flow on set_color: send_header(UPDATE_MODE?) Actually its set_color sends:
#   requestMode? No. It sends RESIZE? Let's think: d.set_color -> setMode('Direct')? 
# The KEY thing openrgb-python does after ANY command: it READS the reply in its reader thread.
# Its set_color implementation sends UPDATE_MODE (to switch to custom) via mode.py? 
# Simplest test matrix: after RED on dev0, try:
#   A) REQUEST_CONTROLLER_COUNT
#   B) REQUEST_CONTROLLER_DATA dev0
send_pkt(0, 1100)
time.sleep(0.3)
send_pkt(0, 1050, struct.pack("<I", 1) + bytes([255, 0, 0, 0]))
print("RED OK")
time.sleep(0.5)

try:
    send_pkt(0, 0)  # REQUEST_CONTROLLER_COUNT
    _, _, p = recv_pkt()
    print("A) COUNT after update:", struct.unpack('<I', p)[0], "- OK")
except OSError as e:
    print("A) COUNT after update FAILED:", e)

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

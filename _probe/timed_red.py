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
send_pkt(0, 50, b"timed-red\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# RED then BLACK with 1.5s gap: this is exactly selftest timing. Does it survive?
send_pkt(0, 1100)
time.sleep(0.5)
send_pkt(0, 1050, struct.pack("<I", 1) + bytes([255, 0, 0, 0]))
print("RED OK")
time.sleep(1.5)
try:
    send_pkt(0, 1050, struct.pack("<I", 1) + bytes([0, 0, 0, 0]))
    print("BLACK OK")
except OSError as e:
    print("BLACK FAILED:", e)
time.sleep(1)
print("alive:", proc.poll() is None)

# now rapid 30fps-like stream on a NEW connection if alive
if proc.poll() is None:
    s.close()
    s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(3)
    send_pkt(0, 40, struct.pack("<I", 4))
    try: recv_pkt()
    except socket.timeout: pass
    send_pkt(0, 50, b"stream\0")
    sent = 0
    t0 = time.time()
    try:
        while time.time() - t0 < 3:
            t = time.time() - t0
            r = int(127 + 127 * __import__('math').sin(t * 3))
            g = int(127 + 127 * __import__('math').sin(t * 3 + 2))
            b = int(127 + 127 * __import__('math').sin(t * 3 + 4))
            send_pkt(0, 1050, struct.pack("<I", 1) + bytes([r, g, b, 0]))
            sent += 1
            time.sleep(0.033)
        print(f"stream: {sent} frames sent OK")
    except OSError as e:
        print(f"stream FAILED after {sent} frames:", e)
    time.sleep(1)
    print("alive:", proc.poll() is None)

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

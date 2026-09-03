import socket, struct, time, subprocess, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(10)
assert proc.poll() is None

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
    assert hdr[:4] == b"ORGB", hdr[:4]
    dev, ptype = struct.unpack("<II", hdr[4:12])
    size = struct.unpack("<I", hdr[12:16])[0]
    return dev, ptype, recv_exact(size)

# v4 handshake (fall back to v0 silently like the reference client if no reply)
send_pkt(0, 40, struct.pack("<I", 4))
try:
    dev, ptype, p = recv_pkt()
    proto = struct.unpack("<I", p)[0]
    print(f"proto reply: dev={dev} type={ptype} server_max={proto} -> we use min(4, {proto})")
except socket.timeout:
    proto = 0
    print("proto: server silent -> v0")

send_pkt(0, 50, b"hexdump-test\0")   # SET_CLIENT_NAME
send_pkt(0, 0)                        # REQUEST_CONTROLLER_COUNT
dev, ptype, p = recv_pkt()
count = struct.unpack("<I", p)[0]
print(f"count={count}")

for idx in range(count):
    send_pkt(idx, 1, struct.pack("<I", min(proto, 4)))
    dev, ptype, d = recv_pkt()
    print(f"\n=== device {idx}: {len(d)} bytes ===")
    hexs = d.hex().upper()
    for i in range(0, min(len(hexs), 640), 32):
        off = i // 2
        line = ' '.join(hexs[i:i+2] for i in range(i, min(i+32, len(hexs)), 2))
        print(f"{off:4d}: {line}")
    # try textual decode
    try:
        import re
        txt = re.findall(rb'[ -~]{4,}', d)
        print("strings:", [t.decode() for t in txt][:10])
    except Exception as e:
        print(e)

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

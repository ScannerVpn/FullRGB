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

# handshake with SHORT timeout, then DRAIN everything the server pushes
s.settimeout(1.5)
send_pkt(0, 40, struct.pack("<I", 4))
try:
    dev, ptype, p = recv_pkt()
    print(f"proto reply: type={ptype}")
except socket.timeout:
    print("proto: silent")
send_pkt(0, 50, b"drain\0")

# drain unsolicited for 3 seconds
s.settimeout(3)
drained = 0
try:
    while True:
        dev, ptype, p = recv_pkt()
        drained += 1
        print(f"unsolicited: dev={dev} type={ptype} size={len(p)}")
except (socket.timeout, EOFError):
    pass
print(f"drained {drained} unsolicited packets")

# now the real request
send_pkt(0, 0)
dev, ptype, p = recv_pkt()
print(f"count={struct.unpack('<I', p)[0]}")

send_pkt(0, 1, struct.pack("<I", 4))
dev, ptype, d = recv_pkt()
print(f"data: type={ptype} size={len(d)}")
print("first 24 bytes:", d[:24].hex(' '))

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

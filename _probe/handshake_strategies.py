import subprocess, socket, time, struct, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(9)
assert proc.poll() is None, f"DIED rc={proc.returncode}"
print("server alive, starting handshake strategies\n")

def recv_some(sock, want, timeout):
    sock.settimeout(timeout)
    buf = b""
    try:
        while len(buf) < want:
            c = sock.recv(want - len(buf))
            if not c: break
            buf += c
    except socket.timeout:
        pass
    return buf

def hdr_hex(b):
    return ' '.join(f'{x:02X}' for x in b[:16])

# S1: proto ver 0
s = socket.create_connection(("127.0.0.1", 6742), timeout=5)
s.sendall(b"ORGB" + struct.pack("<I", 2) + struct.pack("<I", 0))
r = recv_some(s, 64, 4)
print(f"S1 proto_ver=0      -> {len(r)}B: {hdr_hex(r) if r else '(SILENT)'}")
s.close()

# S2: proto ver 1
s = socket.create_connection(("127.0.0.1", 6742), timeout=5)
s.sendall(b"ORGB" + struct.pack("<I", 2) + struct.pack("<I", 1))
r = recv_some(s, 64, 4)
print(f"S2 proto_ver=1      -> {len(r)}B: {hdr_hex(r) if r else '(SILENT)'}")
s.close()

# S3: count only, no handshake
s = socket.create_connection(("127.0.0.1", 6742), timeout=5)
s.sendall(b"ORGB" + struct.pack("<I", 100))
r = recv_some(s, 64, 4)
print(f"S3 count, no handsk -> {len(r)}B: {hdr_hex(r) if r else '(SILENT)'}")
s.close()

# S4: proto ver 1 then count
s = socket.create_connection(("127.0.0.1", 6742), timeout=5)
s.sendall(b"ORGB" + struct.pack("<I", 2) + struct.pack("<I", 1))
time.sleep(0.5)
s.sendall(b"ORGB" + struct.pack("<I", 100))
r = recv_some(s, 64, 5)
print(f"S4 ver1 then count  -> {len(r)}B: {hdr_hex(r) if r else '(SILENT)'}")
s.close()

print(f"\nserver alive: {proc.poll() is None}")
proc.kill()
os.system('taskkill /F /IM OpenRGB.exe 2>nul')

import subprocess, socket, time, struct, os

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
PORT = 16742
proc = subprocess.Popen([EXE, '--server', '--server-port', str(PORT)])
print(f"launched with --server --server-port {PORT}, pid={proc.pid}")
time.sleep(9)
if proc.poll() is not None:
    print(f"DIED even on alt port: rc={proc.returncode}")
    raise SystemExit
print("ALIVE on alt port -> confirms something watches port 6742 specifically")
try:
    s = socket.create_connection(("127.0.0.1", PORT), timeout=5); s.settimeout(5)
    s.sendall(b"Noli" + struct.pack("<I", 2) + struct.pack("<I", 0))
    hdr = b""
    while len(hdr) < 8: hdr += s.recv(8 - len(hdr))
    assert hdr[:4] == b"Noli"
    pid = struct.unpack("<I", hdr[4:])[0]
    payload = s.recv(4)
    print(f"handshake OK on {PORT}: pkt={pid} proto={struct.unpack('<I', payload)[0]}")
    s.sendall(b"Noli" + struct.pack("<I", 100))
    hdr = b""
    while len(hdr) < 8: hdr += s.recv(8 - len(hdr))
    cnt = struct.unpack("<I", s.recv(4))[0]
    print(f"controller_count={cnt}")
    s.close()
except Exception as e:
    print("SDK test failed:", e)
time.sleep(3)
print(f"alive at end: {proc.poll() is None}")
proc.kill()

# catch the port-6742 watcher in the act
print("\n--- catching 6742 watcher ---")
proc2 = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(7)
out = os.popen('netstat -ano | findstr 6742').read()
print(out if out else '(no connections on 6742)')
time.sleep(2)
out2 = os.popen('netstat -ano | findstr 6742').read()
print("after 2s:", out2 if out2 else '(gone)')
for ln in (out + out2).splitlines():
    parts = ln.split()
    if len(parts) >= 5 and parts[3] == 'ESTABLISHED':
        owner = os.popen(f'tasklist /FI "PID eq {parts[4]}" /FO CSV /NH').read().strip()
        print(f"ESTABLISHED owner pid={parts[4]}: {owner}")
proc2.kill()

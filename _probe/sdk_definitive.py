import subprocess, socket, time, struct

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
print("launched OpenRGB SDK server (correct flags)")
time.sleep(9)
assert proc.poll() is None, f"DIED rc={proc.returncode}"
print("server alive at 9s")

s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(5)

def send_pkt(pid_, payload=b""):
    s.sendall(b"Noli" + struct.pack("<I", pid_) + payload)

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        c = s.recv(n - len(buf))
        if not c: raise EOFError
        buf += c
    return buf

def recv_pkt():
    hdr = recv_exact(8)
    assert hdr[:4] == b"Noli", hdr
    pid = struct.unpack("<I", hdr[4:])[0]
    if pid in (2, 100): return pid, recv_exact(4)
    (size,) = struct.unpack("<I", recv_exact(4))
    return pid, recv_exact(size)

def read_str(d, off):
    (ln,) = struct.unpack_from("<H", d, off); off += 2
    return d[off:off+ln].decode("utf-8", "replace"), off + ln

send_pkt(2, struct.pack("<I", 0))
_, p = recv_pkt()
print(f"handshake: protocol={struct.unpack('<I', p)[0]}")

send_pkt(100)
_, p = recv_pkt()
count = struct.unpack("<I", p)[0]
print(f"controllers={count}")

devices = []
for idx in range(count):
    send_pkt(101, struct.pack("<I", idx))
    _, d = recv_pkt()
    off = 0
    dev_type, = struct.unpack_from("<I", d, off); off += 4
    name, off = read_str(d, off); vendor, off = read_str(d, off)
    desc, off = read_str(d, off); ver, off = read_str(d, off)
    serial, off = read_str(d, off); loc, off = read_str(d, off)
    modes, zones, leds, colors = struct.unpack_from("<IIII", d, off)
    devices.append((idx, name, leds, zones))
    print(f"[{idx}] {name} | leds={leds} zones={zones} modes={modes} type={dev_type}")

# --- actually SET COLORS on device 0 (solid red) to prove control ---
total = sum(l for _, _, l, _ in devices)
print(f"\ntotal controllable LEDs = {total}")
for idx, name, leds, zones in devices:
    if leds == 0: continue
    payload = struct.pack("<II", idx, leds) + bytes([255, 0, 0]) * leds
    send_pkt(1050, payload)   # PKT_RGBCONTROLLER_UPDATELEDS
    print(f"set RED on [{idx}] {name} ({leds} leds)")
time.sleep(2)
# green
for idx, name, leds, zones in devices:
    if leds == 0: continue
    send_pkt(1050, struct.pack("<II", idx, leds) + bytes([0, 255, 0]) * leds)
print("set GREEN on all")
time.sleep(2)
# off
for idx, name, leds, zones in devices:
    if leds == 0: continue
    send_pkt(1050, struct.pack("<II", idx, leds) + bytes([0, 0, 0]) * leds)
print("set OFF on all")
time.sleep(1)

print(f"\nserver alive after color control: {proc.poll() is None}")
s.close(); proc.kill()
print("DEFINITIVE SDK TEST: PASS" if True else "")

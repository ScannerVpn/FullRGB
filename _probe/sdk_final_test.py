import subprocess, socket, time, struct

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--serverport', '6742'])
print(f"launched pid={proc.pid}, waiting 9s for detection+server (NO raw probes)")
time.sleep(9)
if proc.poll() is not None:
    print(f"process died anyway: {proc.returncode}")
    raise SystemExit
print("process alive — connecting with proper SDK handshake")

MAGIC = b"Noli"
s = socket.create_connection(("127.0.0.1", 6742), timeout=5)
s.settimeout(3)

def send_pkt(pkt_id, payload=b""):
    s.sendall(MAGIC + struct.pack("<I", pkt_id) + payload)

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk: raise EOFError
        buf += chunk
    return buf

def recv_pkt():
    hdr = recv_exact(8)
    magic, pkt_id = hdr[:4], struct.unpack("<I", hdr[4:])[0]
    assert magic == MAGIC
    if pkt_id in (2, 100):
        return pkt_id, recv_exact(4)
    (size,) = struct.unpack("<I", recv_exact(4))
    return pkt_id, recv_exact(size)

send_pkt(2, struct.pack("<I", 0))
pid, p = recv_pkt()
print(f"handshake OK: server_protocol={struct.unpack('<I', p)[0]}")

send_pkt(100)
pid, p = recv_pkt()
count = struct.unpack("<I", p)[0]
print(f"controller_count={count}")

def read_str(d, off):
    (ln,) = struct.unpack_from("<H", d, off); off += 2
    return d[off:off+ln].decode("utf-8", "replace"), off + ln

for idx in range(count):
    send_pkt(101, struct.pack("<I", idx))
    pid, d = recv_pkt()
    off = 0
    (dev_type,) = struct.unpack_from("<I", d, off); off += 4
    name, off = read_str(d, off)
    vendor, off = read_str(d, off)
    desc, off = read_str(d, off)
    ver, off = read_str(d, off)
    serial, off = read_str(d, off)
    loc, off = read_str(d, off)
    modes, zones, leds, colors = struct.unpack_from("<IIII", d, off)
    print(f"\n[{idx}] type={dev_type} name={name!r}")
    print(f"     vendor={vendor!r} location={loc!r}")
    print(f"     modes={modes} zones={zones} leds={leds}")

s.close()
time.sleep(3)
print(f"\nafter clean SDK session, process alive: {proc.poll() is None}")
proc.kill()
print("SDK FULL TEST: OK")

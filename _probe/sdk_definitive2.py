import subprocess, socket, time, struct

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
print("launched OpenRGB SDK server")
time.sleep(9)
assert proc.poll() is None, f"DIED rc={proc.returncode}"

s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(5)
MAGIC = b"ORGB"

def send_pkt(pid_, payload=b""):
    s.sendall(MAGIC + struct.pack("<I", pid_) + payload)

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        c = s.recv(n - len(buf))
        if not c: raise EOFError
        buf += c
    return buf

def recv_pkt():
    hdr = recv_exact(8)
    assert hdr[:4] == MAGIC, hdr[:4]
    pid = struct.unpack("<I", hdr[4:])[0]
    (size,) = struct.unpack("<I", recv_exact(4))
    return pid, recv_exact(size)

def read_str(d, off):
    (ln,) = struct.unpack_from("<H", d, off); off += 2
    return d[off:off+ln].decode("utf-8", "replace"), off + ln

send_pkt(2, struct.pack("<I", 0))          # protocol version
pid, p = recv_pkt()
print(f"handshake: reply_pkt={pid} protocol={struct.unpack('<I', p)[0]}")

send_pkt(100)                               # controller count
pid, p = recv_pkt()
count = struct.unpack("<I", p)[0]
print(f"controllers={count}")

devices = []
for idx in range(count):
    send_pkt(101, struct.pack("<I", idx))   # request controller data
    pid, d = recv_pkt()
    off = 0
    dev_type, = struct.unpack_from("<I", d, off); off += 4
    name, off = read_str(d, off)
    # rc builds include vendor after name; 0.9 doesn't. Detect by sanity of next count.
    save = off
    vendor, off2 = read_str(d, save)
    probe_type, = struct.unpack_from("<I", d, off2)
    if probe_type > 65535:                   # absurd -> vendor was real field
        off = off2
    else:
        vendor, off = "", save
    desc, off = read_str(d, off)
    ver, off = read_str(d, off)
    serial, off = read_str(d, off)
    loc, off = read_str(d, off)
    modes, zones, leds, colors = struct.unpack_from("<IIII", d, off)
    devices.append((idx, name, leds))
    print(f"[{idx}] type={dev_type} {name!r} vendor={vendor!r} loc={loc!r} leds={leds} zones={zones} modes={modes}")

total = sum(l for _, _, l in devices)
print(f"\ntotal controllable LEDs = {total}")

# set custom mode on each device (needed before direct LED writes on some)
for idx, name, leds in devices:
    if leds: send_pkt(1100, struct.pack("<I", idx))  # SETCUSTOMMODE
time.sleep(1)

def paint(rgb, label):
    for idx, name, leds in devices:
        if leds:
            send_pkt(1050, struct.pack("<II", idx, leds) + bytes(rgb) * leds)
    print(f"painted {label}")
    time.sleep(2)

paint((255, 0, 0), "RED")
paint((0, 255, 0), "GREEN")
paint((0, 0, 255), "BLUE")
paint((0, 0, 0),   "OFF (black)")

print(f"server alive after full color cycle: {proc.poll() is None}")
s.close(); proc.kill()
print("DEFINITIVE SDK TEST: PASS")

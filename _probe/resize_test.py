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

def rs(d, p):
    ln = struct.unpack_from('<H', d, p)[0]
    if ln == 0: return "", p + 2
    return d[p+2:p+2+ln-1].decode('utf-8', 'replace'), p + 2 + ln

def get_dev(idx):
    send_pkt(idx, 1, struct.pack("<I", 4))
    _, _, d = recv_pkt()
    p = 4
    struct.unpack_from('<i', d, p)[0]; p += 4
    name, p = rs(d, p)
    for _ in range(5): _, p = rs(d, p)
    mc = struct.unpack_from('<H', d, p)[0]; p += 2
    p += 4
    for m in range(mc):
        _, p = rs(d, p)
        p += 48
        nc = struct.unpack_from('<H', d, p)[0]; p += 2
        p += nc * 4
    zc = struct.unpack_from('<H', d, p)[0]; p += 2
    zones = []
    for z in range(zc):
        zn, p = rs(d, p)
        zt = struct.unpack_from('<i', d, p)[0]; p += 4
        zmin = struct.unpack_from('<I', d, p)[0]; p += 4
        zmax = struct.unpack_from('<I', d, p)[0]; p += 4
        zcnt = struct.unpack_from('<I', d, p)[0]; p += 4
        ms = struct.unpack_from('<H', d, p)[0]; p += 2
        mh = mw = 0
        if ms > 0:
            mh = struct.unpack_from('<I', d, p)[0]; p += 4
            mw = struct.unpack_from('<I', d, p)[0]; p += 4
            p += mh * mw * 4
        sc = struct.unpack_from('<H', d, p)[0]; p += 2
        for _ in range(sc):
            _, p = rs(d, p); p += 12
        zones.append((zn, zt, zmin, zmax, zcnt))
    lc = struct.unpack_from('<H', d, p)[0]; p += 2
    return name, zones, lc

send_pkt(0, 40, struct.pack("<I", 4))
try: recv_pkt()
except socket.timeout: pass
send_pkt(0, 50, b"resize\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

print("=== BEFORE resize ===")
for i in range(count):
    n, zs, lc = get_dev(i)
    print(f"[{i}] {n}: leds={lc}")
    for z in zs: print("    ", z)

# RESIZE_ZONE = 1000, payload: i32 zone_index + u32 new_size
print("\n=== resizing ===")
for i in range(count):
    n, zs, lc = get_dev(i)
    for zi, (zn, zt, zmin, zmax, zcnt) in enumerate(zs):
        if zmax > 0 and zcnt == 0:
            target = zmax
            send_pkt(i, 1000, struct.pack("<iI", zi, target))
            print(f"  resize dev{i} zone{zi} {zn!r} -> {target}")
            time.sleep(0.4)

time.sleep(2)
print("\n=== AFTER resize ===")
for i in range(count):
    n, zs, lc = get_dev(i)
    print(f"[{i}] {n}: leds={lc}")
    for z in zs: print("    ", z)

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

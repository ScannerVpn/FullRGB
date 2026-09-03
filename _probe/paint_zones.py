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
        _, p = rs(d, p); p += 48
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

def upd_leds(dev, colors):
    body = struct.pack("<H", len(colors)) + b"".join(bytes(c) + b"\x00" for c in colors)
    send_pkt(dev, 1050, struct.pack("<I", len(body) + 4) + body)

send_pkt(0, 40, struct.pack("<I", 4))
try: recv_pkt()
except socket.timeout: pass
send_pkt(0, 50, b"paintzone\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# resize all zones that report 0 leds to their max
for i in range(count):
    n, zs, lc = get_dev(i)
    for zi, (zn, zt, zmin, zmax, zcnt) in enumerate(zs):
        if zmax > 0 and zcnt == 0:
            send_pkt(i, 1000, struct.pack("<iI", zi, zmax))
            time.sleep(0.3)
time.sleep(2)

# now paint with real LED counts
counts = {}
for i in range(count):
    n, zs, lc = get_dev(i)
    counts[i] = lc
    print(f"dev{i} {n}: {lc} leds")
    if lc:
        send_pkt(i, 1100)  # SET_CUSTOM_MODE
time.sleep(0.8)

import math
print("\npainting rainbow sweep for 6 seconds - WATCH YOUR PC")
t0 = time.time()
frames = 0
try:
    while time.time() - t0 < 6:
        tt = time.time() - t0
        for i, lc in counts.items():
            if not lc: continue
            cols = []
            for j in range(lc):
                h = ((j / max(1, lc)) + tt * 0.5) % 1.0
                k = int(h * 6) % 6
                f = h * 6 - math.floor(h * 6)
                v, pp, q, t_ = 255, 0, int(255 * (1 - f)), int(255 * f)
                rgb = [(v, t_, pp), (q, v, pp), (pp, v, t_), (pp, q, v), (t_, pp, v), (v, pp, q)][k]
                cols.append(rgb)
            upd_leds(i, cols)
        frames += 1
        time.sleep(0.05)
    print(f"OK: {frames} frames painted across {sum(counts.values())} LEDs")
except OSError as e:
    print(f"FAILED after {frames} frames: {e}")

# turn off
for i, lc in counts.items():
    if lc: upd_leds(i, [(0, 0, 0)] * lc)
time.sleep(0.5)
print("alive:", proc.poll() is None)
s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

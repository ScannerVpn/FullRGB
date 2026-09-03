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
send_pkt(0, 50, b"zoneinfo\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

def rs(d, p):
    ln = struct.unpack_from('<H', d, p)[0]
    if ln == 0: return "", p + 2
    return d[p+2:p+2+ln-1].decode('utf-8', 'replace'), p + 2 + ln

for idx in range(count):
    send_pkt(idx, 1, struct.pack("<I", 4))
    _, _, d = recv_pkt()
    p = 4
    dev_type = struct.unpack_from('<i', d, p)[0]; p += 4
    name, p = rs(d, p)
    for _ in range(5):
        _, p = rs(d, p)
    mc = struct.unpack_from('<H', d, p)[0]; p += 2
    active = struct.unpack_from('<i', d, p)[0]; p += 4
    mode_names = []
    for m in range(mc):
        mn, p = rs(d, p)
        mode_names.append(mn)
        p += 4 * 12
        nc = struct.unpack_from('<H', d, p)[0]; p += 2
        p += nc * 4
    zc = struct.unpack_from('<H', d, p)[0]; p += 2
    print(f"\n=== [{idx}] {name} | active_mode={active} ({mode_names[active] if active < len(mode_names) else '?'}) ===")
    print(f"    modes: {mode_names}")
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
        print(f"    zone[{z}] {zn!r:26} type={zt} min={zmin} max={zmax} count={zcnt} matrix={mh}x{mw} segs={sc}")
    lc = struct.unpack_from('<H', d, p)[0]; p += 2
    print(f"    total device LEDs = {lc}")

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

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
send_pkt(0, 50, b"parselog\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

def parse_device(d, label, verbose=False):
    import struct as st
    def rs2(p):
        ln = st.unpack_from('<H', d, p)[0]
        if ln == 0: return "", p+2
        return d[p+2:p+2+ln-1].decode(), p+2+ln
    p = 4
    dev_type = st.unpack_from('<i', d, p)[0]; p += 4
    name, p = rs2(p)
    vendor, p = rs2(p)
    desc, p = rs2(p)
    ver, p = rs2(p)
    serial, p = rs2(p)
    loc, p = rs2(p)
    mode_count = st.unpack_from('<H', d, p)[0]; p += 2
    active = st.unpack_from('<I', d, p)[0]; p += 4
    for m in range(mode_count):
        mn, p = rs2(p)
        p += 4*10  # value, flags, smin, smax, bmin, bmax, cmin, cmax, speed, bval
        p += 4     # direction
        p += 4     # cmode
        nc = st.unpack_from('<H', d, p)[0]; p += 2
        p += nc * 4
    zone_count = st.unpack_from('<H', d, p)[0]; p += 2
    zones = []
    for z in range(zone_count):
        zn, p = rs2(p)
        ztype = st.unpack_from('<i', d, p)[0]; p += 4
        zmin = st.unpack_from('<I', d, p)[0]; p += 4
        zmax = st.unpack_from('<I', d, p)[0]; p += 4
        zcnt = st.unpack_from('<I', d, p)[0]; p += 4
        # matrix: u32 h, u32 w, h*w u32 (no u16 size prefix per ZoneData.unpack!)
        # but how do we know if matrix present? zone_type == 3 (MATRIX)? Pump type=2 though had 7x7!
        # openrgb-python: matrix_zone_size = parse_var('H') ALWAYS, matrix parsed if type==MATRIX
        msize = st.unpack_from('<H', d, p)[0]; p += 2
        has_matrix = msize > 0
        if has_matrix:
            h = st.unpack_from('<I', d, p)[0]; p += 4
            w = st.unpack_from('<I', d, p)[0]; p += 4
            p += h * w * 4
        seg_count = st.unpack_from('<H', d, p)[0]; p += 2
        for sgi in range(seg_count):
            _, p = rs2(p)
            p += 12
        if verbose: print(f"  zone[{z}] {zn!r} type={ztype} count={zcnt} msize={msize} segs={seg_count} p={p}")
        zones.append(zn)
    led_count = st.unpack_from('<H', d, p)[0]; p += 2
    leds = []
    for l in range(led_count):
        ln_, p = rs2(p)
        p += 4
        leds.append(ln_)
    color_count = st.unpack_from('<H', d, p)[0]; p += 2
    p += color_count * 4
    ok = p == len(d)
    print(f"{label}: {name!r} modes={mode_count} zones={zone_count}{zones} leds={led_count} colors={color_count} END p={p}/{len(d)} {'PERFECT' if ok else 'MISMATCH '+str(len(d)-p)}")

for idx in range(count):
    send_pkt(idx, 1, struct.pack("<I", 4))
    _, _, dd = recv_pkt()
    try:
        parse_device(dd, f"dev{idx}", verbose=(idx==1))
    except Exception as e:
        print(f"dev{idx} FAILED: {e}")

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

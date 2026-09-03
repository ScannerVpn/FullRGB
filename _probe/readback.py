"""Write colors, then READ BACK the stored colors from the server to prove
whether UPDATE_LEDS is actually accepted and applied per device/zone."""
import socket, struct, time, subprocess, os

os.system('taskkill /F /IM OpenRGB.exe >nul 2>&1')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(9)

s = socket.create_connection(('127.0.0.1', 6742), timeout=15)
s.settimeout(15)

def send(dev, typ, payload=b''):
    s.sendall(b'ORGB' + struct.pack('<III', dev, typ, len(payload)) + payload)

def read_pkt():
    h = b''
    while len(h) < 16:
        c = s.recv(16 - len(h))
        if not c: raise IOError('closed')
        h += c
    dev, typ, size = struct.unpack('<III', h[4:16])
    body = b''
    while len(body) < size:
        c = s.recv(size - len(body))
        if not c: raise IOError('closed')
        body += c
    return dev, typ, body

send(0, 40, struct.pack('<I', 4)); _, _, r = read_pkt()
proto = min(struct.unpack('<I', r[:4])[0], 4)
send(0, 50, b'DIAG\0')
send(0, 0); _, _, r = read_pkt()
count = struct.unpack('<I', r[:4])[0]

class R:
    def __init__(s_, d): s_.d, s_.p = d, 0
    def u16(s_):
        v = struct.unpack_from('<H', s_.d, s_.p)[0]; s_.p += 2; return v
    def u32(s_):
        v = struct.unpack_from('<I', s_.d, s_.p)[0]; s_.p += 4; return v
    def i32(s_):
        v = struct.unpack_from('<i', s_.d, s_.p)[0]; s_.p += 4; return v
    def st(s_):
        n = s_.u16()
        if n == 0: return ''
        v = s_.d[s_.p:s_.p+n-1].decode('utf-8', 'replace'); s_.p += n; return v
    def skip(s_, n): s_.p += n

def parse(d):
    r = R(d); r.skip(4)
    dtype = r.i32()
    name = r.st(); r.st(); r.st(); r.st(); r.st(); r.st()
    mc = r.u16(); active = r.i32()
    modes = []
    for _ in range(mc):
        mn = r.st(); val = r.i32(); flags = r.u32(); r.skip(4*10)
        cc = r.u16(); r.skip(cc*4)
        modes.append((mn, val, flags))
    zc = r.u16(); zones = []
    for _ in range(zc):
        zn = r.st(); zt = r.i32()
        lo, hi, cnt = r.u32(), r.u32(), r.u32()
        ms = r.u16()
        if ms > 0:
            h, w = r.u32(), r.u32(); r.skip(h*w*4)
        sc = r.u16()
        for _ in range(sc):
            r.st(); r.skip(12)
        zones.append((zn, zt, lo, hi, cnt))
    lc = r.u16()
    for _ in range(lc):
        r.st(); r.skip(4)
    # device colors (current)
    ccount = r.u16()
    cols = []
    for _ in range(ccount):
        b0, b1, b2, b3 = s_bytes = r.d[r.p:r.p+4]; r.p += 4
        cols.append((b0, b1, b2))
    return dict(type=dtype, name=name, modes=modes, zones=zones, leds=lc,
                active=active, colors=cols)

def get(i):
    send(i, 1, struct.pack('<I', proto)); _, _, d = read_pkt()
    return parse(d)

devs = [get(i) for i in range(count)]
print(f'devices={count}')
for i, d in enumerate(devs):
    print(f'[{i}] {d["name"]} leds={d["leds"]} colors_stored={len(d["colors"])} '
          f'active_mode={d["active"]}({d["modes"][d["active"]][0] if d["modes"] else "?"}) '
          f'first3={d["colors"][:3]}')

def update_leds(dev, colors):
    body = struct.pack('<H', len(colors))
    for (rr, gg, bb) in colors:
        body += bytes((rr, gg, bb, 0))
    send(dev, 1050, struct.pack('<I', 4 + len(body)) + body)

print('\n--- test A: SET_CUSTOM_MODE then paint GREEN, then read back ---')
for i, d in enumerate(devs):
    if d['leds'] > 0:
        send(i, 1100)
time.sleep(0.4)
for i, d in enumerate(devs):
    if d['leds'] > 0:
        update_leds(i, [(0, 255, 0)] * d['leds'])
time.sleep(1.2)
for i, d in enumerate(devs):
    if d['leds'] == 0: continue
    after = get(i)
    ok = after['colors'][:3] == [(0, 255, 0)] * min(3, len(after['colors']))
    print(f'[{i}] {after["name"]}: stored_first3={after["colors"][:3]} '
          f'match_green={ok} active_mode={after["active"]}')

print('\n--- holding GREEN for 12s: LOOK AT THE PC NOW ---')
t0 = time.time()
while time.time() - t0 < 12:
    for i, d in enumerate(devs):
        if d['leds'] > 0:
            update_leds(i, [(0, 255, 0)] * d['leds'])
    time.sleep(0.1)
print('done. openrgb alive:', proc.poll() is None)
proc.kill()

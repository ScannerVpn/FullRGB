"""Reproduce exactly what the app does: connect -> expand zones -> refresh ->
set custom mode -> stream 30fps. Detect when/why the socket dies."""
import socket, struct, time, subprocess, os, math, sys

os.system('taskkill /F /IM OpenRGB.exe >nul 2>&1')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(9)

s = socket.create_connection(('127.0.0.1', 6742), timeout=15)
s.settimeout(15)

def hdr(dev, typ, size):
    return b'ORGB' + struct.pack('<III', dev, typ, size)

def send(dev, typ, payload=b''):
    s.sendall(hdr(dev, typ, len(payload)) + payload)

def read_pkt():
    h = b''
    while len(h) < 16:
        c = s.recv(16 - len(h))
        if not c: raise IOError('closed')
        h += c
    assert h[:4] == b'ORGB', h[:4]
    dev, typ, size = struct.unpack('<III', h[4:16])
    body = b''
    while len(body) < size:
        c = s.recv(size - len(body))
        if not c: raise IOError('closed')
        body += c
    return dev, typ, body

# handshake
send(0, 40, struct.pack('<I', 4))
_, _, r = read_pkt()
proto = min(struct.unpack('<I', r[:4])[0], 4)
send(0, 50, b'DIAG\0')
send(0, 0)
_, _, r = read_pkt()
count = struct.unpack('<I', r[:4])[0]
print(f'proto={proto} devices={count}')

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
    name = r.st(); vendor = r.st(); desc = r.st(); ver = r.st(); ser = r.st(); loc = r.st()
    mc = r.u16(); active = r.i32()
    modes = []
    for _ in range(mc):
        mn = r.st(); val = r.i32(); flags = r.u32()
        r.skip(4*10)  # 12 u32 total incl value/flags -> 10 remaining
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
    lc = r.u16(); leds = []
    for _ in range(lc):
        leds.append(r.st()); r.skip(4)
    return dict(type=dtype, name=name, modes=modes, zones=zones, leds=lc, active=active)

def enumerate_devs(n):
    out = []
    for i in range(n):
        send(i, 1, struct.pack('<I', proto))
        _, _, d = read_pkt()
        out.append(parse(d))
    return out

devs = enumerate_devs(count)
for i, d in enumerate(devs):
    print(f'[{i}] {d["name"]} leds={d["leds"]} active_mode={d["active"]}')
    for j, m in enumerate(d['modes']):
        print(f'      mode[{j}] {m[0]!r} value={m[1]} flags=0x{m[2]:x}')
    for j, z in enumerate(d['zones']):
        print(f'      zone[{j}] {z[0]!r} type={z[1]} min={z[2]} max={z[3]} count={z[4]}')

# expand zones that are empty
changed = False
for i, d in enumerate(devs):
    for j, z in enumerate(d['zones']):
        if z[3] > 0 and z[4] == 0:
            send(i, 1000, struct.pack('<iI', j, z[3]))
            changed = True
            time.sleep(0.12)
if changed:
    time.sleep(0.6)
    # THIS is the app's behaviour: refresh right after resize
    send(0, 0)
    _, _, r = read_pkt()
    count = struct.unpack('<I', r[:4])[0]
    devs = enumerate_devs(count)
    print('--- after expand ---')
    for i, d in enumerate(devs):
        print(f'[{i}] {d["name"]} leds={d["leds"]}')

# set custom mode per device
for i, d in enumerate(devs):
    if d['leds'] > 0:
        send(i, 1100)
time.sleep(0.3)

def update_leds(dev, colors):
    body = struct.pack('<H', len(colors))
    for (rr, gg, bb) in colors:
        body += bytes((rr, gg, bb, 0))
    payload = struct.pack('<I', 4 + len(body)) + body
    send(dev, 1050, payload)

def hsv(h):
    i = int(h*6) % 6; f = h*6 - math.floor(h*6)
    v, p, q, t = 255, 0, int(255*(1-f)), int(255*f)
    return [(v,t,p),(q,v,p),(p,v,t),(p,q,v),(t,p,v),(v,p,q)][i]

print('--- streaming 30fps, no reads (mimics app) ---')
t0 = time.time()
frames = 0
try:
    while time.time() - t0 < 25:
        ph = (time.time() - t0) * 0.3
        for i, d in enumerate(devs):
            n = d['leds']
            if n == 0: continue
            update_leds(i, [hsv(((ph + k/n) % 1.0)) for k in range(n)])
        frames += 1
        time.sleep(1/30)
except Exception as e:
    print(f'DIED after {frames} frames / {time.time()-t0:.1f}s: {type(e).__name__}: {e}')
else:
    print(f'survived {frames} frames / {time.time()-t0:.1f}s')

# how much unread data is queued from the server?
s.settimeout(0.5)
queued = 0
try:
    while True:
        c = s.recv(65536)
        if not c: break
        queued += len(c)
except Exception:
    pass
print(f'unread bytes queued from server: {queued}')
print('openrgb alive:', proc.poll() is None)
proc.kill()

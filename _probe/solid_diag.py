"""
Diagnose the 'solid colour differs per zone' report.

Paints an EXACT solid colour to every zone using the same wire format the app uses,
then reads the controller data back and prints the stored colour of every LED,
grouped per zone. If the readback is uniform, the difference is in the hardware /
LED chip (needs per-zone calibration). If it is not uniform, our writes are wrong.
"""
import socket, struct, time, subprocess, os
from collections import Counter

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
send(0, 50, b'SOLIDDIAG\0')
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
        mn = r.st(); r.i32(); r.u32(); r.skip(4*10)
        cc = r.u16(); r.skip(cc*4)
        modes.append(mn)
    zc = r.u16(); zones = []
    for _ in range(zc):
        zn = r.st(); zt = r.i32()
        lo, hi, cnt = r.u32(), r.u32(), r.u32()
        ms = r.u16()
        mh = mw = 0
        if ms > 0:
            mh, mw = r.u32(), r.u32(); r.skip(mh*mw*4)
        sc = r.u16()
        for _ in range(sc):
            r.st(); r.skip(12)
        zones.append(dict(name=zn, type=zt, min=lo, max=hi, count=cnt, mh=mh, mw=mw))
    lc = r.u16()
    for _ in range(lc):
        r.st(); r.skip(4)
    ccount = r.u16()
    cols = []
    for _ in range(ccount):
        cols.append(tuple(r.d[r.p:r.p+3])); r.p += 4
    return dict(type=dtype, name=name, modes=modes, active=active,
                zones=zones, leds=lc, colors=cols)

def get(i):
    send(i, 1, struct.pack('<I', proto)); _, _, d = read_pkt()
    return parse(d)

devs = [get(i) for i in range(count)]

# expand zones like the app does
changed = False
for i, d in enumerate(devs):
    for j, z in enumerate(d['zones']):
        if z['max'] > z['min'] and z['count'] != z['max']:
            send(i, 1000, struct.pack('<ii', j, z['max']))
            changed = True; time.sleep(0.12)
if changed:
    time.sleep(0.6)
    devs = [get(i) for i in range(count)]

print('=== devices ===')
for i, d in enumerate(devs):
    print(f"[{i}] {d['name']} leds={d['leds']} active_mode={d['active']}({d['modes'][d['active']] if d['modes'] else '?'})")
    for j, z in enumerate(d['zones']):
        print(f"     zone[{j}] {z['name']!r} type={z['type']} count={z['count']} matrix={z['mh']}x{z['mw']}")

# force Direct
for i, d in enumerate(devs):
    if d['leds'] > 0:
        send(i, 1100)
time.sleep(0.4)

def update_zone(dev, zone, colors):
    body = struct.pack('<iH', zone, len(colors))
    for (rr, gg, bb) in colors:
        body += bytes((rr, gg, bb, 0))
    send(dev, 1051, struct.pack('<I', 4 + len(body)) + body)

TEST = (0, 183, 204)   # what Solid #00E5FF at brightness 0.8 produces
print(f'\n=== painting EVERY zone exactly RGB{TEST} via UPDATE_ZONE_LEDS ===')
for i, d in enumerate(devs):
    for j, z in enumerate(d['zones']):
        if z['count'] == 0: continue
        update_zone(i, j, [TEST] * z['count'])
time.sleep(1.2)

print('\n=== readback: stored colour per zone ===')
for i in range(len(devs)):
    d2 = get(i)
    cols = d2['colors']
    off = 0
    print(f"[{i}] {d2['name']}  (stored {len(cols)} colours)")
    for j, z in enumerate(d2['zones']):
        n = z['count']
        seg = cols[off:off+n]
        off += n
        uniq = Counter(seg)
        summary = ', '.join(f'{c}x{k}' for k, c in uniq.most_common(3))
        flag = '' if (len(uniq) == 1 and next(iter(uniq)) == TEST) else '   <<< MISMATCH'
        print(f"     zone[{j}] {z['name']!r:<22} n={n:<4} {summary}{flag}")

print('\nopenrgb alive:', proc.poll() is None)
proc.kill()

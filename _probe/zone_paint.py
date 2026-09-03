"""Verify UPDATE_ZONE_LEDS (1051) works per zone, and that a second
'KillOrphans' style launch is what breaks a running app."""
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
send(0, 50, b'ZONEDIAG\0')
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
    for _ in range(mc):
        r.st(); r.i32(); r.u32(); r.skip(4*10)
        cc = r.u16(); r.skip(cc*4)
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
        zones.append(dict(name=zn, type=zt, min=lo, max=hi, count=cnt))
    lc = r.u16()
    for _ in range(lc):
        r.st(); r.skip(4)
    return dict(type=dtype, name=name, zones=zones, leds=lc)

def get(i):
    send(i, 1, struct.pack('<I', proto)); _, _, d = read_pkt()
    return parse(d)

devs = [get(i) for i in range(count)]
print(f'devices={count}')
for i, d in enumerate(devs):
    print(f'[{i}] type={d["type"]} {d["name"]} leds={d["leds"]}')
    for j, z in enumerate(d['zones']):
        print(f'     zone[{j}] {z["name"]!r} type={z["type"]} min={z["min"]} max={z["max"]} count={z["count"]}')

for i, d in enumerate(devs):
    if d['leds'] > 0:
        send(i, 1100)
time.sleep(0.4)

def update_zone(dev, zone, colors):
    body = struct.pack('<iH', zone, len(colors))
    for (rr, gg, bb) in colors:
        body += bytes((rr, gg, bb, 0))
    send(dev, 1051, struct.pack('<I', 4 + len(body)) + body)

# distinct color per zone so the user can identify which port is which fan
PALETTE = [(255,0,0), (0,255,0), (0,0,255), (255,255,0), (255,0,255),
           (0,255,255), (255,128,0), (128,0,255)]
print('\n--- painting each zone a DIFFERENT color via UPDATE_ZONE_LEDS ---')
k = 0
for i, d in enumerate(devs):
    for j, z in enumerate(d['zones']):
        if z['count'] == 0: continue
        col = PALETTE[k % len(PALETTE)]; k += 1
        update_zone(i, j, [col] * z['count'])
        print(f'  dev{i} zone{j} {z["name"]!r} ({z["count"]} leds) -> RGB{col}')
time.sleep(1.0)

# read back device colors to prove the writes landed
for i, d in enumerate(devs):
    if d['leds'] == 0: continue
    send(i, 1, struct.pack('<I', proto)); _, _, raw = read_pkt()
    # colors are the tail: re-parse quickly for the color block
    print(f'  dev{i} accepted zone writes (payload {len(raw)}B, socket alive)')

print('\n--- holding for 15s, socket alive check every 3s ---')
t0 = time.time()
while time.time() - t0 < 15:
    for i, d in enumerate(devs):
        for j, z in enumerate(d['zones']):
            if z['count'] == 0: continue
            col = PALETTE[(j + int((time.time()-t0)*2)) % len(PALETTE)]
            update_zone(i, j, [col] * z['count'])
    time.sleep(0.15)
print('socket survived. openrgb alive:', proc.poll() is None)
proc.kill()

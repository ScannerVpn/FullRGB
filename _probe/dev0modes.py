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

class R:
    def __init__(self, d): self.d = d; self.p = 0
    def byte(self):
        if self.p >= len(self.d): raise EOFError("EOF")
        v = self.d[self.p]; self.p += 1; return v
    def u16(self): return self.byte() | (self.byte() << 8)
    def u32(self): return self.byte() | (self.byte() << 8) | (self.byte() << 16) | (self.byte() << 24)
    def i32(self):
        v = self.u32()
        return v - (1 << 32) if v >= (1 << 31) else v
    def skip(self, n): self.p += n
    def string(self):
        ln = self.u16()
        if ln == 0: return ""
        raw = self.d[self.p:self.p + ln - 1]
        self.p += ln
        return raw.decode('utf-8', 'replace')

send_pkt(0, 1, struct.pack("<I", 4))
_, _, d = recv_pkt()
r = R(d)
print(f"device 0: {len(d)} bytes")
print(f"dev_type={r.i32()}")
name = r.string(); print(f"name={name!r} p={r.p}")
vendor = r.string(); print(f"vendor={vendor!r} p={r.p}")
desc = r.string(); print(f"desc={desc!r} p={r.p}")
ver = r.string(); print(f"version={ver!r} p={r.p}")
serial = r.string(); print(f"serial={serial!r} p={r.p}")
loc = r.string(); print(f"location={loc!r} p={r.p}")
mode_count = r.u16(); print(f"mode_count={mode_count} p={r.p}")
active_mode = r.i32(); print(f"active_mode={active_mode} p={r.p}")
for m in range(mode_count):
    try:
        mn = r.string()
        mv = r.i32()
        mf = r.u32()
        smin = r.u32(); smax = r.u32()
        speed = r.u32() if (mf & (1 << 4)) else None
        direction = r.u32()
        color_mode = r.u32()
        ncolors = 0
        if color_mode != 0:
            cmin = r.i32(); cmax = r.i32(); clen = r.i32()
            ncolors = max(0, min(clen, 256))
            for c in range(ncolors): r.skip(3)
        if mf & (1 << 3): r.skip(12)
        if mf & (1 << 8): r.skip(12)
        print(f"mode[{m}] name={mn!r} value={mv} flags=0x{mf:X} speed={speed} cmode={color_mode} ncolors={ncolors} p={r.p}")
    except EOFError:
        print(f"mode[{m}] EOF at p={r.p}, tail hex: {d[r.p:r.p+40].hex(' ')}")
        break

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

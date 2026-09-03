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

send_pkt(1, 1, struct.pack("<I", 4))
_, _, d = recv_pkt()

import struct as st
def rs(p):
    ln = st.unpack_from('<H', d, p)[0]
    if ln == 0: return "", p+2
    return d[p+2:p+2+ln-1].decode(), p+2+ln

# zone[0] 'Pump' name@251 ends 258. type@258=2, min@262=0, max@266=0, count@270=0
# matrix u16@274 = CC 00 = 204?? BUT zone_type=2 (SINGLE per openrgb enum? 1=single,2=linear?)
# unpack shows: matrix_zone_size read ALWAYS (u16); matrix parsed ONLY IF zone_type==MATRIX(3?)
# msize=204 can't be right for a linear zone. Print raw 270-280 again:
print("270-280:", ' '.join(f'{b:02X}' for b in d[270:280]))
# hmm 274=CC? earlier row said '273: 00 CC 00 07 00 00 00 07' -> 273=00, 274=CC, 275=00, 276=07
# u16@274 = CC 00 = 204. But maybe count is u16@270?? then fields shift by 2:
# type@258(4), min@262(4), max@266(4), count@270 as U32=0, msize u16@274 = 204...
# OR: count@270 = u32 0, then SEGMENTS u16@274? But segments come after matrix... and mlen u16 must be there.
# What if zone[0] really has matrix u16 = 204? and zone_type==MATRIX? type=2 though.
# OR maybe the matrix size u16 read is actually 'CC 00' = 204 -> then h=00 07 00 00=1792?? no.
# Try: dev1 zone fields might be: type i32@258, min@262, max@266, count@270, mlen u16@274=204.
# If mlen=204 interpreted as flat count -> 204*4 = 816 bytes of matrix -> way past 684. Bogus.
# Suspect my mode parse for dev1 over-consumed by 1 byte somewhere. mode0 nc=1 -> 4 bytes RGBA.
# fields: 11 u32s + name(7) + nc(2) + 4 = from 190: name 190-197, value..cmode = 197+44=241? 
# Actually let me recount: p=190 after active_mode. name@190: ln=7 -> ends 199? 190+2+7=199.
# Wait earlier print: mode0 name='Direct' fields=[...] nc=1 p=245 -> after colors p=249.
# name@190: ln=7 means 'Direct'+NUL=7 -> 190+2+7=199. Then 11 u32s = 44 -> 199+44=243. nc u16@243 -> 245. nc=1 -> +4 = 249. OK.
# zone_count u16@249 = 7. zone[0] name@251: ln=5 -> 251+2+5=258. type@258=2 -> 262. min@262=0 -> 266.
# max@266=0 -> 270. count@270=0 -> 274. mlen u16@274 = 0x00CC = 204.
# Everything checks out EXCEPT mlen=204 for a non-matrix zone. UNLESS 'Pump' zone in Commander Core
# v2.0.19 firmware reports MATRIX with weird data? No...
# Alternative: fields are min, max, count but AS I32 and count@270 = 0. same.
# What if zone has EXTRA field in v4 before matrix: 'start_idx'? No, unpack shows none.
# Let me just print what openrgb-python parses for dev1 zones:
from openrgb import orgb
from openrgb.orgb import OpenRGBClient
c = OpenRGBClient("127.0.0.1", 6742)
for dev in c.devices:
    print(dev.name, "| zones:", [(z.name, z.type, z.num_leds, z.mat_height, z.mat_width) for z in dev.zones])
    print("  leds:", [l.name for l in dev.leds][:6], "...")
    print("  modes:", [m.name for m in dev.modes])

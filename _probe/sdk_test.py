import socket, struct, time, sys

MAGIC = b"Noli"
PKT_PROTOCOL_VERSION = 2
PKT_REQUEST_CONTROLLER_COUNT = 100
PKT_REQUEST_CONTROLLER_DATA = 101

s = socket.create_connection(("127.0.0.1", 6742), timeout=5)
s.settimeout(3)

def send_pkt(pkt_id, payload=b""):
    s.sendall(MAGIC + struct.pack("<I", pkt_id) + payload)

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise EOFError("closed")
        buf += chunk
    return buf

def recv_pkt():
    hdr = recv_exact(8)
    magic, pkt_id = hdr[:4], struct.unpack("<I", hdr[4:])[0]
    assert magic == MAGIC, f"bad magic {magic!r}"
    if pkt_id in (2, 100):
        payload = recv_exact(4)
        return pkt_id, payload
    # variable-length: u32 size then data
    (size,) = struct.unpack("<I", recv_exact(4))
    return pkt_id, recv_exact(size)

# 1) protocol handshake
send_pkt(PKT_PROTOCOL_VERSION, struct.pack("<I", 0))
pid, p = recv_pkt()
proto_ver = struct.unpack("<I", p)[0]
print(f"handshake: pkt={pid} server_protocol={proto_ver}")

# 2) controller count
send_pkt(PKT_REQUEST_CONTROLLER_COUNT)
pid, p = recv_pkt()
count = struct.unpack("<I", p)[0]
print(f"controller_count={count}")

def read_str(data, off):
    (ln,) = struct.unpack_from("<H", data, off); off += 2
    return data[off:off+ln].decode("utf-8", "replace"), off + ln

for idx in range(count):
    send_pkt(PKT_REQUEST_CONTROLLER_DATA, struct.pack("<I", idx))
    pid, d = recv_pkt()
    off = 0
    (dev_type,) = struct.unpack_from("<I", d, off); off += 4
    name, off = read_str(d, off)
    vendor, off = read_str(d, off)   # rc protocol: vendor after name
    desc, off = read_str(d, off)
    ver, off = read_str(d, off)
    serial, off = read_str(d, off)
    loc, off = read_str(d, off)
    (modes, zones, leds, colors) = struct.unpack_from("<IIII", d, off)
    print(f"\n[{idx}] type={dev_type} name={name!r} vendor={vendor!r} desc={desc!r}")
    print(f"     version={ver!r} serial={serial!r} location={loc!r}")
    print(f"     modes={modes} zones={zones} leds={leds} colors={colors}")

s.close()
print("\nSDK PROTOCOL TEST: OK")

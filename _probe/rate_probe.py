"""Measures how fast the OpenRGB server accepts UPDATE_ZONE_LEDS on this rig.

Answers one question: is the ~25 fps ceiling in FullRGB's loop, or in the server/HID path?
Run with the SDK server already listening (start FullRGB or OpenRGB --server first).
"""
import socket, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051


def send(s, dev, ptype, payload=b""):
    s.sendall(MAGIC + struct.pack("<III", dev, ptype, len(payload)) + payload)


def recv(s):
    hdr = b""
    while len(hdr) < 16:
        chunk = s.recv(16 - len(hdr))
        if not chunk:
            raise IOError("closed")
        hdr += chunk
    assert hdr[:4] == MAGIC, hdr[:4]
    _dev, ptype, size = struct.unpack("<III", hdr[4:])
    body = b""
    while len(body) < size:
        chunk = s.recv(size - len(body))
        if not chunk:
            raise IOError("closed")
        body += chunk
    return ptype, body


def rd_str(b, o):
    (n,) = struct.unpack_from("<H", b, o)
    return b[o + 2:o + 2 + n - 1].decode("utf-8", "replace"), o + 2 + n


def parse(b):
    o = 4
    (_dtype,) = struct.unpack_from("<i", b, o); o += 4
    name, o = rd_str(b, o)
    for _ in range(5):
        _v, o = rd_str(b, o)
    (mcount,) = struct.unpack_from("<H", b, o); o += 2
    o += 4
    for _ in range(mcount):
        _mn, o = rd_str(b, o)
        o += 4 * 12
        (cc,) = struct.unpack_from("<H", b, o); o += 2 + cc * 4
    (zcount,) = struct.unpack_from("<H", b, o); o += 2
    zones = []
    for zi in range(zcount):
        zn, o = rd_str(b, o)
        (_zt, _mn2, _mx, cnt) = struct.unpack_from("<iIII", b, o); o += 16
        (msz,) = struct.unpack_from("<H", b, o); o += 2
        if msz:
            h, w = struct.unpack_from("<II", b, o); o += 8 + h * w * 4
        (sc,) = struct.unpack_from("<H", b, o); o += 2
        for _ in range(sc):
            _sn, o = rd_str(b, o)
            o += 12
        zones.append((zi, zn, cnt))
    return name, zones


def main():
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(10)
    send(s, 0, SET_NAME, b"probe\0")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])
    devs = []
    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s)
        name, zones = parse(body)
        devs.append((i, name, [z for z in zones if z[2] > 0]))
        print(f"dev {i}: {name} — {[(z[1], z[2]) for z in zones if z[2] > 0]}")

    frames = 0
    t0 = time.perf_counter()
    deadline = t0 + 6.0
    per_zone = []
    while time.perf_counter() < deadline:
        for di, _n, zones in devs:
            for zi, _zn, cnt in zones:
                buf = bytearray()
                for k in range(cnt):
                    buf += bytes(((k * 7) % 256, (k * 3) % 256, (frames * 5) % 256, 0))
                payload = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
                w0 = time.perf_counter()
                send(s, di, UPDATE_ZONE_LEDS, payload)
                per_zone.append(time.perf_counter() - w0)
        frames += 1
    dt = time.perf_counter() - t0

    zone_writes = len(per_zone)
    print(f"\nfull-frame passes: {frames} in {dt:.2f}s -> {frames / dt:.1f} fps")
    print(f"zone writes: {zone_writes} -> {zone_writes / dt:.0f} writes/s")
    per_zone.sort()
    print(f"per-zone write: avg {sum(per_zone) / zone_writes * 1000:.2f} ms  "
          f"p50 {per_zone[zone_writes // 2] * 1000:.2f} ms  "
          f"p99 {per_zone[int(zone_writes * 0.99)] * 1000:.2f} ms  "
          f"max {per_zone[-1] * 1000:.2f} ms")
    s.close()


if __name__ == "__main__":
    main()

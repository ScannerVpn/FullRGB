"""Compares per-ZONE writes vs whole-DEVICE writes, and reports blocking behaviour.

The engine writes every zone separately (12 writes/frame on this rig). If a whole-device
UPDATE_LEDS costs one USB transaction instead of one per zone, the engine is paying 6x.
This measures both, and prints the stall distribution so socket backpressure is visible.
"""
import socket, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051


def send(s, dev, ptype, payload=b""):
    s.sendall(MAGIC + struct.pack("<III", dev, ptype, len(payload)) + payload)


def recv(s):
    hdr = b""
    while len(hdr) < 16:
        c = s.recv(16 - len(hdr))
        if not c:
            raise IOError("closed")
        hdr += c
    _d, ptype, size = struct.unpack("<III", hdr[4:])
    body = b""
    while len(body) < size:
        c = s.recv(size - len(body))
        if not c:
            raise IOError("closed")
        body += c
    return ptype, body


def rd_str(b, o):
    (n,) = struct.unpack_from("<H", b, o)
    return b[o + 2:o + 2 + n - 1].decode("utf-8", "replace"), o + 2 + n


def parse(b):
    o = 4
    o += 4
    name, o = rd_str(b, o)
    for _ in range(5):
        _v, o = rd_str(b, o)
    (mcount,) = struct.unpack_from("<H", b, o); o += 2 + 4
    for _ in range(mcount):
        _mn, o = rd_str(b, o)
        o += 48
        (cc,) = struct.unpack_from("<H", b, o); o += 2 + cc * 4
    (zcount,) = struct.unpack_from("<H", b, o); o += 2
    zones = []
    for zi in range(zcount):
        zn, o = rd_str(b, o)
        _zt, _mn2, _mx, cnt = struct.unpack_from("<iIII", b, o); o += 16
        (msz,) = struct.unpack_from("<H", b, o); o += 2
        if msz:
            h, w = struct.unpack_from("<II", b, o); o += 8 + h * w * 4
        (sc,) = struct.unpack_from("<H", b, o); o += 2
        for _ in range(sc):
            _sn, o = rd_str(b, o)
            o += 12
        zones.append((zi, zn, cnt))
    (ledc,) = struct.unpack_from("<H", b, o)
    return name, zones, ledc


def stats(times, label, passes, dt):
    times.sort()
    n = len(times)
    print(f"{label}: {passes} passes in {dt:.2f}s -> {passes / dt:.1f} fps   ({n} writes)")
    print(f"   write ms: p50 {times[n // 2] * 1000:.2f}  p90 {times[int(n * .9)] * 1000:.2f}  "
          f"p99 {times[int(n * .99)] * 1000:.2f}  max {times[-1] * 1000:.0f}")
    over = [t for t in times if t > 0.05]
    print(f"   writes blocked >50ms: {len(over)}  total stalled {sum(over):.2f}s of {dt:.2f}s")


def main():
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(20)
    send(s, 0, SET_NAME, b"probe2\0")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])
    devs = []
    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s)
        name, zones, ledc = parse(body)
        devs.append((i, name, [z for z in zones if z[2] > 0], ledc))
        print(f"dev {i}: {name} leds={ledc} zones={[(z[1], z[2]) for z in zones if z[2] > 0]}")
    print()

    SECS = 6.0

    # ---- A: per-zone writes (what the engine does today) ----
    t0 = time.perf_counter(); passes = 0; times = []
    while time.perf_counter() - t0 < SECS:
        for di, _n, zones, _lc in devs:
            for zi, _zn, cnt in zones:
                buf = bytearray()
                for k in range(cnt):
                    buf += bytes(((k + passes) % 256, 40, 90, 0))
                p = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
                w = time.perf_counter(); send(s, di, UPDATE_ZONE_LEDS, p); times.append(time.perf_counter() - w)
        passes += 1
    stats(times, "A per-zone ", passes, time.perf_counter() - t0)
    print()

    # ---- B: whole-device writes ----
    t0 = time.perf_counter(); passes = 0; times = []
    while time.perf_counter() - t0 < SECS:
        for di, _n, _zones, lc in devs:
            buf = bytearray()
            for k in range(lc):
                buf += bytes((90, (k + passes) % 256, 40, 0))
            p = struct.pack("<IH", 6 + lc * 4, lc) + bytes(buf)
            w = time.perf_counter(); send(s, di, UPDATE_LEDS, p); times.append(time.perf_counter() - w)
        passes += 1
    stats(times, "B whole-dev", passes, time.perf_counter() - t0)
    s.close()


if __name__ == "__main__":
    main()

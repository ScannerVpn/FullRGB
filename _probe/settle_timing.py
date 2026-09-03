"""Settle it: is the Corsair's 50 ms per zone write real USB time, or an artifact of my ack scheme?

The contradiction, all on the Commander Core:
  zone_count_scaling.py  fresh socket, 1 zone write + ack, 4 s : 49.93 ms EVERY time
  timing_states.py       reused socket, 1 zone write + ack, 25x: 0.09 ms median (max 26.8)
  timing_states.py       reused socket, 7 zone writes + ack    : 350 ms  (= 7 x 50)

If 50 ms is real device time, the Commander Core simply cannot exceed ~3 fps for a 7-zone frame
and FullRGB should stop pretending otherwise. If it is an artifact, the engine is leaving a lot
of smoothness on the table.

Everything below runs on ONE socket, interleaved, so socket age and connection count cannot
explain the difference. Configurations, 120 samples each, reported as full percentile spreads:
  A  1 zone write, ack
  B  7 zone writes, ack
  C  7 zone writes, NO ack           (how fast does the server ACCEPT them?)
  D  7 zone writes, ack every 4th frame
  E  1 zone write, ack, IDENTICAL colour every time (does the server skip unchanged frames?)
"""
import socket, statistics, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051
SAMPLES = 120


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
    o = 8
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
    return name, [z for z in zones if z[2] > 0]


def main():
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(120)
    send(s, 0, SET_NAME, b"settle\0")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])
    target = None
    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s)
        name, zones = parse(body)
        if "commander" in name.lower() or "corsair" in name.lower():
            target = (i, name, zones)
    if target is None:
        print("no Corsair device")
        return
    di, name, zones = target
    print(f"{name}: {len(zones)} zones, ONE socket for every configuration\n")

    def ack():
        send(s, 0, REQ_PROTO, struct.pack("<I", 4))
        recv(s)

    def zone(zi, cnt, colour):
        send(s, di, UPDATE_ZONE_LEDS,
             struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(colour + (0,)) * cnt)

    def report(label, samples):
        samples.sort()
        n = len(samples)
        q = lambda p: samples[min(int(n * p), n - 1)]
        print(f"  {label:44} p10={q(.1):7.2f} p50={q(.5):7.2f} p90={q(.9):7.2f} "
              f"max={samples[-1]:7.2f} ms")

    # A: one zone write + ack
    out = []
    for i in range(SAMPLES):
        zi, _zn, cnt = zones[i % len(zones)]
        t = time.perf_counter()
        zone(zi, cnt, (i % 256, 30, 90))
        ack()
        out.append((time.perf_counter() - t) * 1000)
    report("A  1 zone + ack", out)

    # B: full frame + ack
    out = []
    for i in range(SAMPLES):
        t = time.perf_counter()
        for zi, _zn, cnt in zones:
            zone(zi, cnt, (i % 256, 40, 100))
        ack()
        out.append((time.perf_counter() - t) * 1000)
    report("B  7 zones + ack", out)

    # C: full frame, no ack (server acceptance rate only)
    out = []
    t0 = time.perf_counter()
    for i in range(SAMPLES):
        t = time.perf_counter()
        for zi, _zn, cnt in zones:
            zone(zi, cnt, (i % 256, 50, 110))
        out.append((time.perf_counter() - t) * 1000)
    noack_total = time.perf_counter() - t0
    report("C  7 zones, no ack", out)
    print(f"     (accepted {SAMPLES / noack_total:.0f} frames/s without acking — then drain)")
    ack()   # let the backlog finish before the next configuration

    # D: full frame, ack every 4th
    out = []
    for i in range(SAMPLES):
        t = time.perf_counter()
        for zi, _zn, cnt in zones:
            zone(zi, cnt, (i % 256, 60, 120))
        if i % 4 == 3:
            ack()
        out.append((time.perf_counter() - t) * 1000)
    ack()
    report("D  7 zones, ack every 4th frame", out)

    # E: identical colour every time
    out = []
    for i in range(SAMPLES):
        zi, _zn, cnt = zones[i % len(zones)]
        t = time.perf_counter()
        zone(zi, cnt, (7, 7, 7))
        ack()
        out.append((time.perf_counter() - t) * 1000)
    report("E  1 zone + ack, identical colour", out)

    s.close()


if __name__ == "__main__":
    main()

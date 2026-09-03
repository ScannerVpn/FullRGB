"""Resolve the timing contradiction: what exactly does a Corsair frame cost?

Contradictory measurements so far (same device, same packet type):
  contention_check.py   1 zone write + ack, tight loop, fresh socket : 50.0 ms every time
  timing_states.py      1 zone write + ack, tight loop, reused socket:  0.09 ms
  timing_states.py      7 zone writes + ack (a full frame)           : 350 ms
  ack_granularity.py    1 zone write + ack, round robin              : 49 ms

No design decision is trustworthy until that is explained, so this measures the one variable
that actually differs: HOW MANY zone writes are batched before the ack, plus whether the socket
is fresh. Steady state over 4 s per configuration, first sample reported separately (warm-up).

If cost ≈ k × 50 ms, the device is charged per zone write and batching does not help.
If cost ≈ 50 ms regardless of k, the charge is per frame and writing all zones is free.
"""
import socket, statistics, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051
SECS = 4.0


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


def sock(tag):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(60)
    send(s, 0, SET_NAME, f"scale-{tag}\0".encode())
    return s


def main():
    s0 = sock("enum")
    send(s0, 0, REQ_COUNT)
    _, body = recv(s0)
    (count,) = struct.unpack("<I", body[:4])
    target = None
    for i in range(count):
        send(s0, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s0)
        name, zones = parse(body)
        if "commander" in name.lower() or "corsair" in name.lower():
            target = (i, name, zones)
    s0.close()
    if target is None:
        print("no Corsair device")
        return
    di, name, zones = target
    print(f"{name}: zones={[(z[1], z[2]) for z in zones]}\n")
    print(f"{'zones/frame':>12} {'frames/s':>9} {'ms/frame p50':>13} {'ms/frame p90':>13} "
          f"{'first':>8} {'ms/zone':>8}")

    for k in range(1, len(zones) + 1):
        s = sock(f"k{k}")
        samples = []
        t0 = time.perf_counter()
        i = 0
        while time.perf_counter() - t0 < SECS:
            t = time.perf_counter()
            for j in range(k):
                zi, _zn, cnt = zones[(i + j) % len(zones)]
                send(s, di, UPDATE_ZONE_LEDS,
                     struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(((i + j) % 256, 25, 80, 0)) * cnt)
            send(s, 0, REQ_PROTO, struct.pack("<I", 4))
            recv(s)
            samples.append((time.perf_counter() - t) * 1000)
            i += k
        dt = time.perf_counter() - t0
        s.close()
        first = samples[0]
        steady = sorted(samples[1:]) or samples
        p50 = statistics.median(steady)
        p90 = steady[int(len(steady) * .9)]
        print(f"{k:>12} {len(samples) / dt:9.1f} {p50:13.2f} {p90:13.2f} {first:8.2f} {p50 / k:8.2f}")

    print("\nSame table for the ASUS board, as a control:")
    s0 = sock("enum2")
    send(s0, 0, REQ_COUNT)
    _, body = recv(s0)
    (count,) = struct.unpack("<I", body[:4])
    asus = None
    for i in range(count):
        send(s0, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s0)
        name, zones2 = parse(body)
        if "asus" in name.lower() or "maximus" in name.lower():
            asus = (i, name, zones2)
    s0.close()
    if asus is None:
        return
    di2, name2, zones2 = asus
    print(f"{name2}: zones={len(zones2)}")
    for k in (1, len(zones2)):
        s = sock(f"a{k}")
        samples = []
        t0 = time.perf_counter()
        i = 0
        while time.perf_counter() - t0 < SECS:
            t = time.perf_counter()
            for j in range(k):
                zi, _zn, cnt = zones2[(i + j) % len(zones2)]
                send(s, di2, UPDATE_ZONE_LEDS,
                     struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(((i + j) % 256, 25, 80, 0)) * cnt)
            send(s, 0, REQ_PROTO, struct.pack("<I", 4))
            recv(s)
            samples.append((time.perf_counter() - t) * 1000)
            i += k
        dt = time.perf_counter() - t0
        s.close()
        steady = sorted(samples[1:]) or samples
        p50 = statistics.median(steady)
        print(f"{k:>12} {len(samples) / dt:9.1f} {p50:13.2f} {'':13} {samples[0]:8.2f} {p50 / k:8.2f}")


if __name__ == "__main__":
    main()

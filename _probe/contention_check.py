"""Do whole-device UPDATE_LEDS packets reach the HARDWARE, or are they server-side no-ops?

The puzzle: with completion acks, ONE whole-device UPDATE_LEDS costs 0.15 ms on the Corsair
Commander Core, while a single zone write costs ~49 ms. 0.15 ms cannot include a USB transaction,
yet applied_check.py shows the server DID store all 233 colours. So either
  (a) the server applies the colours to its controller object and never writes USB (no-op), or
  (b) a background thread coalesces the writes and pushes them at the device's real rate.

Distinguishing test WITHOUT eyes — contention:
  socket A floods whole-device writes as fast as the server accepts them
  socket B measures single zone writes (real USB, ~49 ms) with completion acks

If A's packets cause hardware writes, B's zone writes must contend for the same HID handle and
get measurably slower. If A is a no-op, B's timing is unchanged.

Baseline (A idle) is measured first, then the same measurement with A flooding.
"""
import socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051
MEASURE = 6.0


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
    (ledc,) = struct.unpack_from("<H", b, o)
    return name, [z for z in zones if z[2] > 0], ledc


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
    send(s, 0, SET_NAME, f"contend-{tag}\0".encode())
    return s


def measure_zone_writes(di, zones, label):
    """Single zone write + ack, repeatedly. Returns per-write ms samples."""
    s = sock(f"measure-{label}")
    samples = []
    t0 = time.perf_counter()
    i = 0
    try:
        while time.perf_counter() - t0 < MEASURE:
            zi, _zn, cnt = zones[i % len(zones)]
            payload = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((i % 256, 30, 90, 0)) * cnt
            w = time.perf_counter()
            send(s, di, UPDATE_ZONE_LEDS, payload)
            send(s, 0, REQ_PROTO, struct.pack("<I", 4))
            recv(s)
            samples.append((time.perf_counter() - w) * 1000)
            i += 1
    finally:
        try:
            s.close()
        except Exception:
            pass
    return samples


def main():
    s0 = sock("enum")
    send(s0, 0, REQ_COUNT)
    _, body = recv(s0)
    (count,) = struct.unpack("<I", body[:4])
    target = None
    for i in range(count):
        send(s0, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s0)
        name, zones, ledc = parse(body)
        if "commander" in name.lower() or "corsair" in name.lower():
            target = (i, name, zones, ledc)
    s0.close()
    if target is None:
        print("no Corsair device")
        return
    di, name, zones, ledc = target
    print(f"target dev{di} {name}: {len(zones)} zones, {ledc} leds\n")

    def report(label, samples):
        samples.sort()
        n = len(samples)
        print(f"{label:34} {n / MEASURE:6.1f} writes/s   "
              f"p50={samples[n // 2]:7.2f} p90={samples[int(n * .9)]:7.2f} max={samples[-1]:7.2f} ms")

    # 1) baseline
    report("baseline (nothing else running)", measure_zone_writes(di, zones, "base"))

    # 2) with a whole-device flood on another socket
    stop = threading.Event()
    flooded = [0]

    def flood():
        s = sock("flood")
        i = 0
        try:
            while not stop.is_set():
                payload = struct.pack("<IH", 6 + ledc * 4, ledc) + bytes((90, 20, i % 256, 0)) * ledc
                send(s, di, UPDATE_LEDS, payload)
                send(s, 0, REQ_PROTO, struct.pack("<I", 4))
                recv(s)
                i += 1
        except Exception:
            pass
        flooded[0] = i
        try:
            s.close()
        except Exception:
            pass

    t = threading.Thread(target=flood, daemon=True)
    t.start()
    time.sleep(0.5)
    samples = measure_zone_writes(di, zones, "contend")
    stop.set()
    t.join(timeout=3)
    report("during whole-device flood", samples)
    print(f"\nwhole-device packets accepted during the window: {flooded[0]} "
          f"({flooded[0] / (MEASURE + 0.5):.0f}/s)")
    print("\nInterpretation: if the two rows match, whole-device writes never touch the hardware.")


if __name__ == "__main__":
    main()

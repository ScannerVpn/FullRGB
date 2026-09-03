"""Are whole-device UPDATE_LEDS packets DROPPED, or QUEUED as real hardware work?

Facts so far on the Corsair Commander Core:
  - one zone write + completion ack costs a very consistent ~50 ms  (=> ~20 Hz device rate limit)
  - one whole-device UPDATE_LEDS + ack costs ~0.1 ms and the server stores all 233 colours
  - 9848 whole-device writes/s is far beyond 20 Hz, so they cannot each be doing USB work

Two possibilities remain:
  DROPPED   the handler updates the server's colour array and never touches the device
  QUEUED    the handler marks the controller dirty and a device thread pushes it at ~20 Hz
            (in which case whole-device writes DO light the hardware, just at 20 Hz)

Discriminator, no eyes needed — all on ONE socket, which the server processes in order:
  A  zone write + ack                                (baseline, known real USB work)
  B  1 whole-device write, then zone write + ack
  C  10 whole-device writes, then zone write + ack

  A ≈ B ≈ C            => whole-device writes cost the device nothing  => DROPPED
  B ≈ 2A, C ≈ 11A      => each queued a real update                    => QUEUED
"""
import socket, struct, statistics, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051
REPS = 12


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
    send(s, 0, SET_NAME, f"queue-{tag}\0".encode())
    return s


def main():
    s = sock("main")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])
    target = None
    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s)
        name, zones, ledc = parse(body)
        if "commander" in name.lower() or "corsair" in name.lower():
            target = (i, name, zones, ledc)
    if target is None:
        print("no Corsair device")
        return
    di, name, zones, ledc = target
    print(f"target dev{di} {name}: {len(zones)} zones, {ledc} leds")
    print(f"all packets on ONE socket, processed in order\n")

    def ack():
        send(s, 0, REQ_PROTO, struct.pack("<I", 4))
        recv(s)

    def whole(i):
        send(s, di, UPDATE_LEDS,
             struct.pack("<IH", 6 + ledc * 4, ledc) + bytes((70, 20, i % 256, 0)) * ledc)

    def zone(i):
        zi, _zn, cnt = zones[i % len(zones)]
        send(s, di, UPDATE_ZONE_LEDS,
             struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((i % 256, 25, 80, 0)) * cnt)

    def run(label, prefix_count):
        samples = []
        for i in range(REPS):
            for k in range(prefix_count):
                whole(i * 100 + k)
            t = time.perf_counter()
            zone(i)
            ack()
            samples.append((time.perf_counter() - t) * 1000)
        med = statistics.median(samples)
        print(f"{label:44} median={med:8.2f} ms   min={min(samples):7.2f} max={max(samples):8.2f}")
        return med

    a = run("A  zone write + ack", 0)
    b = run("B  1 whole-device write, then zone + ack", 1)
    c = run("C  10 whole-device writes, then zone + ack", 10)

    print()
    print(f"B/A = {b / a:.2f}   C/A = {c / a:.2f}")
    if c < a * 2:
        print("=> whole-device writes add NO device work: they are server-side only (DROPPED).")
        print("   The engine must keep per-zone writes for this device.")
    else:
        print("=> whole-device writes queue REAL updates (QUEUED): they do drive the hardware,")
        print("   so one whole-device write per frame is the fast path.")
    s.close()


if __name__ == "__main__":
    main()

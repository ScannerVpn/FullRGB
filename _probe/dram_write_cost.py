"""Does a Corsair-style whole-device write also cheat on the ENE DRAM modules?

The DIMMs deliver 20-21 fps out of 30 (dropped ~10/s), i.e. one 8-LED zone write costs ~15-20 ms.
Established for the Corsair: a whole-device UPDATE_LEDS is a server-side no-op (queued_or_dropped.py
showed 10 of them add zero device time). The DIMMs have exactly ONE zone, so if UPDATE_LEDS is
honest for them, it is the cheaper packet and the engine could use it for single-zone devices.

Same discriminator as before, on one socket (server processes it in order):
  A  zone write + ack
  B  10 whole-device writes, then zone write + ack
  A ~= B  => whole-device writes cost the device nothing => server-side only, keep zone writes.
"""
import socket, statistics, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051
REPS = 20


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
    (dtype,) = struct.unpack_from("<i", b, o); o += 4
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
    return name, dtype, [z for z in zones if z[2] > 0], ledc


def main():
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(60)
    send(s, 0, SET_NAME, b"dram-cost\0")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])

    def ack():
        send(s, 0, REQ_PROTO, struct.pack("<I", 4))
        recv(s)

    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, b = recv(s)
        name, dtype, zones, ledc = parse(b)
        if dtype != 1:
            continue
        print(f"\n=== dev{i} {name}: {len(zones)} zone(s), {ledc} leds ===")

        def zone(k):
            for zi, _zn, cnt in zones:
                send(s, i, UPDATE_ZONE_LEDS,
                     struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((k % 256, 40, 90, 0)) * cnt)

        def whole(k):
            send(s, i, UPDATE_LEDS,
                 struct.pack("<IH", 6 + ledc * 4, ledc) + bytes((90, k % 256, 40, 0)) * ledc)

        def run(label, prefix):
            samples = []
            for k in range(REPS):
                for j in range(prefix):
                    whole(k * 100 + j)
                t = time.perf_counter()
                zone(k)
                ack()
                samples.append((time.perf_counter() - t) * 1000)
            med = statistics.median(samples)
            print(f"  {label:40} median={med:7.2f} ms  min={min(samples):6.2f} max={max(samples):7.2f}")
            return med

        a = run("A  zone write + ack", 0)
        b2 = run("B  10 whole-device writes + zone + ack", 10)
        print(f"  B/A = {b2 / a:.2f}  ->  " +
              ("whole-device writes are FREE = server-side only" if b2 < a * 2
               else "whole-device writes cost real device time"))

    s.close()


if __name__ == "__main__":
    main()

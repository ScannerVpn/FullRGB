"""VISUAL A/B: does a whole-device UPDATE_LEDS actually light the Corsair, or only zone writes?

Why it matters: with completion acks, 7 zone writes cost the Commander Core ~343 ms (≈3 fps),
while ONE whole-device write costs 0.15 ms. The server reports both as applied (applied_check.py),
but the server's stored colour array is not proof that the USB write happened — and earlier rounds
concluded that fans/pump only respond to zone writes. Only eyes can settle it.

Timeline (starts after a 6 s countdown so the user can look at the case):
  phase A  0-12 s   WHOLE-DEVICE writes only: RED, GREEN, BLUE, 4 s each
  gap      12-15 s  everything black via zone writes (proves zone writes work)
  phase B  15-27 s  PER-ZONE writes: RED, GREEN, BLUE, 4 s each

If phase A shows colour changes, whole-device writes work and the engine can use them.
If only phase B shows colour, the engine must keep per-zone writes.
"""
import socket, struct, sys, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS, SET_CUSTOM_MODE = 1050, 1051, 1100


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


def ack(s):
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    recv(s)


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
    send(s, 0, SET_NAME, b"visual-ab\0")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])

    target = None
    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s)
        name, zones, ledc = parse(body)
        print(f"dev{i}: {name} leds={ledc} zones={len(zones)}", flush=True)
        if "corsair" in name.lower() or "commander" in name.lower():
            target = (i, name, zones, ledc)
    if target is None:
        print("no Corsair device found", flush=True)
        return 1

    di, name, zones, ledc = target
    send(s, di, SET_CUSTOM_MODE)
    ack(s)
    print(f"target = dev{di} {name}\n", flush=True)

    def whole(rgb):
        payload = struct.pack("<IH", 6 + ledc * 4, ledc) + bytes(rgb + (0,)) * ledc
        send(s, di, UPDATE_LEDS, payload)
        ack(s)

    def per_zone(rgb):
        for zi, _zn, cnt in zones:
            payload = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(rgb + (0,)) * cnt
            send(s, di, UPDATE_ZONE_LEDS, payload)
        ack(s)

    for n in range(6, 0, -1):
        print(f"starting in {n}...", flush=True)
        time.sleep(1)

    colours = [("RED", (255, 0, 0)), ("GREEN", (0, 255, 0)), ("BLUE", (0, 0, 255))]

    print("PHASE A — whole-device writes only", flush=True)
    for label, rgb in colours:
        print(f"  A {label}", flush=True)
        t0 = time.perf_counter()
        while time.perf_counter() - t0 < 4.0:
            whole(rgb)
            time.sleep(0.05)

    print("GAP — black via zone writes", flush=True)
    per_zone((0, 0, 0))
    time.sleep(3)

    print("PHASE B — per-zone writes", flush=True)
    for label, rgb in colours:
        print(f"  B {label}", flush=True)
        t0 = time.perf_counter()
        while time.perf_counter() - t0 < 4.0:
            per_zone(rgb)

    per_zone((0, 0, 0))
    print("done", flush=True)
    s.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())

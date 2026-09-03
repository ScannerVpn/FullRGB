"""Is a whole-device UPDATE_LEDS actually APPLIED, or silently ignored?

ack_granularity.py produced a suspicious result: with completion acks, a whole-device UPDATE_LEDS
costs 0.19 ms on BOTH devices (≈4700 fps) while a single zone write costs 6.4 ms on the ASUS board
and 49 ms on the Corsair. 0.19 ms is too fast for any USB transaction, which suggests the server
is discarding the packet rather than applying it.

This checks it without needing eyes: paint a known pattern, then read the controller back with
REQUEST_CONTROLLER_DATA and compare the stored colours. The server keeps the applied colours in
its controller object, so
  - colours match  => the packet was accepted and applied
  - colours differ => the packet was ignored (a no-op)

Test order per device: zone writes (known-good reference) then whole-device write.
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


def parse_full(b):
    """Returns (name, zones, led_count, colors) — colors are the server's applied values."""
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
    (ledc,) = struct.unpack_from("<H", b, o); o += 2
    for _ in range(ledc):          # LED names
        _ln, o = rd_str(b, o)
        o += 4                      # led value
    (ccount,) = struct.unpack_from("<H", b, o); o += 2
    colors = []
    for _ in range(ccount):
        r, g, bl, _a = struct.unpack_from("<BBBB", b, o); o += 4
        colors.append((r, g, bl))
    return name, [z for z in zones if z[2] > 0], ledc, colors


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
    send(s, 0, SET_NAME, f"applied-{tag}\0".encode())
    return s


def read_dev(s, di):
    send(s, di, REQ_DATA, struct.pack("<I", 4))
    _, body = recv(s)
    return parse_full(body)


def main():
    s = sock("main")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])

    for di in range(count):
        name, zones, ledc, _c = read_dev(s, di)
        print(f"dev{di} {name}: leds={ledc} zones={len(zones)}")

        # ---- reference: per-zone writes with a distinctive colour ----
        for zi, _zn, cnt in zones:
            payload = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((11, 22, 33, 0)) * cnt
            send(s, di, UPDATE_ZONE_LEDS, payload)
        send(s, 0, REQ_PROTO, struct.pack("<I", 4)); recv(s)     # ack
        _n, _z, _l, colors = read_dev(s, di)
        zone_ok = colors[:1] == [(11, 22, 33)] if colors else False
        print(f"   per-zone   write -> stored[0]={colors[0] if colors else None} applied={zone_ok}")

        # ---- candidate: one whole-device write with a different colour ----
        payload = struct.pack("<IH", 6 + ledc * 4, ledc) + bytes((44, 55, 66, 0)) * ledc
        t0 = time.perf_counter()
        send(s, di, UPDATE_LEDS, payload)
        send(s, 0, REQ_PROTO, struct.pack("<I", 4)); recv(s)     # ack
        dt = (time.perf_counter() - t0) * 1000
        _n, _z, _l, colors = read_dev(s, di)
        dev_ok = colors[:1] == [(44, 55, 66)] if colors else False
        print(f"   whole-dev  write -> stored[0]={colors[0] if colors else None} applied={dev_ok} "
              f"({dt:.2f} ms incl ack)")

        # how many LEDs did it actually change?
        if colors:
            changed = sum(1 for c in colors if c == (44, 55, 66))
            print(f"   LEDs set to the whole-device colour: {changed}/{len(colors)}")
        print()

    s.close()


if __name__ == "__main__":
    main()

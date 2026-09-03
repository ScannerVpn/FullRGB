"""How much does the write GRANULARITY cost, measured with completion acks?

ack_flowcontrol.py established that a protocol round-trip is a true completion ack: the server
answers only after it has processed the preceding writes. Sustained rates were
ASUS ~42.6 fps, Corsair ~5.2 fps for per-zone writes (7 zones = ~343 ms/frame ⇒ ~49 ms per zone
write on the Corsair).

This compares, with the same ack pacing:
  Z   one UPDATE_ZONE_LEDS per zone   (what the engine does; known to light fans/pump)
  D   one UPDATE_LEDS for the device  (single transaction per frame)
  Z1  one zone per frame, round robin (does cost scale with zone count or per write?)

The point is to find out whether the Corsair's ~49 ms is per USB transaction or per LED payload,
which decides whether coarser writes would buy real smoothness.
"""
import socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051
WINDOW = 10.0


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


def open_sock(tag):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(60)
    send(s, 0, SET_NAME, f"gran-{tag}\0".encode())
    return s


def worker(di, name, zones, ledc, mode, out):
    s = open_sock(f"dev{di}-{mode}")
    frames = 0
    waits = []
    err = ""
    t0 = time.perf_counter()
    try:
        while time.perf_counter() - t0 < WINDOW:
            w = time.perf_counter()
            if mode == "Z":
                for zi, _zn, cnt in zones:
                    buf = bytearray()
                    for k in range(cnt):
                        buf += bytes(((k + frames) % 256, 20, 60, 0))
                    send(s, di, UPDATE_ZONE_LEDS,
                         struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf))
            elif mode == "D":
                buf = bytearray()
                for k in range(ledc):
                    buf += bytes((60, (k + frames) % 256, 20, 0))
                send(s, di, UPDATE_LEDS, struct.pack("<IH", 6 + ledc * 4, ledc) + bytes(buf))
            else:  # Z1: one zone per frame
                zi, _zn, cnt = zones[frames % len(zones)]
                buf = bytearray()
                for k in range(cnt):
                    buf += bytes(((k + frames) % 256, 20, 60, 0))
                send(s, di, UPDATE_ZONE_LEDS,
                     struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf))

            send(s, 0, REQ_PROTO, struct.pack("<I", 4))
            recv(s)
            waits.append(time.perf_counter() - w)
            frames += 1
    except Exception as e:
        err = f"{type(e).__name__}: {e}"
    out[(di, mode)] = (name, frames, time.perf_counter() - t0, waits, err)
    try:
        s.close()
    except Exception:
        pass


def main():
    s0 = open_sock("enum")
    send(s0, 0, REQ_COUNT)
    _, body = recv(s0)
    (count,) = struct.unpack("<I", body[:4])
    devs = []
    for i in range(count):
        send(s0, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s0)
        name, zones, ledc = parse(body)
        devs.append((i, name, zones, ledc))
        print(f"dev {i}: {name} leds={ledc} zones={len(zones)}")
    s0.close()
    print()

    out = {}
    for mode, label in (("Z", "per-zone (all zones per frame)"),
                        ("D", "whole-device (one write per frame)"),
                        ("Z1", "one zone per frame, round robin")):
        ts = [threading.Thread(target=worker, args=(di, n, z, lc, mode, out))
              for di, n, z, lc in devs]
        for t in ts:
            t.start()
        for t in ts:
            t.join()
        print(f"=== {label} ===")
        for di, name, zones, _lc in devs:
            nm, frames, dt, waits, err = out[(di, mode)]
            waits.sort()
            n = len(waits)
            p50 = waits[n // 2] * 1000 if n else 0
            mx = waits[-1] * 1000 if n else 0
            per_write = p50 / (len(zones) if mode == "Z" else 1)
            print(f"  dev{di} {name[:26]:26} {frames / dt:6.1f} fps   "
                  f"frame ms p50={p50:7.2f} max={mx:7.2f}   per-write≈{per_write:6.2f} ms  {err}")
        print()


if __name__ == "__main__":
    main()

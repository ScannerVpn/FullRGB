"""What write rate can each device ACTUALLY sustain?

Per-device sockets removed the cross-device blocking, but the Corsair Commander Core still
stalled: 7 zone writes per frame at 30 fps is 210 USB transactions/s and the device cannot
drain them, so the socket buffer fills and send() blocks for seconds.

This measures true capacity per device by pushing as fast as the socket accepts (blocking
send, long timeout) for a fixed window: the completed-write rate converges to what the
hardware really consumes. Compares:
  Z  per-zone writes  (UPDATE_ZONE_LEDS, one per zone — what the engine does)
  D  whole-device write (UPDATE_LEDS, one per frame)
"""
import socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051
WINDOW = 12.0


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


def channel(di):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.settimeout(60)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    time.sleep(0.2)
    send(s, 0, SET_NAME, f"cap-dev{di}\0".encode())
    return s


def flood(di, zones, ledc, mode, result):
    s = channel(di)
    frames = 0
    stalls = []
    t0 = time.perf_counter()
    try:
        while time.perf_counter() - t0 < WINDOW:
            w = time.perf_counter()
            if mode == "Z":
                for zi, _zn, cnt in zones:
                    buf = bytearray()
                    for k in range(cnt):
                        buf += bytes(((k + frames) % 256, 20, 60, 0))
                    p = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
                    send(s, di, UPDATE_ZONE_LEDS, p)
            else:
                buf = bytearray()
                for k in range(ledc):
                    buf += bytes((60, (k + frames) % 256, 20, 0))
                p = struct.pack("<IH", 6 + ledc * 4, ledc) + bytes(buf)
                send(s, di, UPDATE_LEDS, p)
            stalls.append(time.perf_counter() - w)
            frames += 1
    except Exception as e:
        result[(di, mode)] = (frames, time.perf_counter() - t0, stalls, f"{type(e).__name__}")
        try:
            s.close()
        except Exception:
            pass
        return
    result[(di, mode)] = (frames, time.perf_counter() - t0, stalls, "")
    s.close()


def enum_channel():
    """Enumeration socket: MUST drain the version reply, or the first recv() returns it."""
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(30)
    send(s, 0, SET_NAME, b"cap-enum\0")
    return s


def main():
    s0 = enum_channel()
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

    for mode, label in (("Z", "per-zone writes"), ("D", "whole-device writes")):
        print(f"=== {label}, flooding {WINDOW:.0f}s per device (one socket each, run in parallel) ===")
        result = {}
        ts = [threading.Thread(target=flood, args=(di, z, lc, mode, result))
              for di, _n, z, lc in devs]
        for t in ts:
            t.start()
        for t in ts:
            t.join()
        for di, name, zones, _lc in devs:
            frames, dt, stalls, err = result[(di, mode)]
            stalls.sort()
            n = len(stalls)
            p50 = stalls[n // 2] * 1000 if n else 0
            p99 = stalls[min(int(n * .99), n - 1)] * 1000 if n else 0
            mx = stalls[-1] * 1000 if n else 0
            print(f"  dev{di} {name[:26]:26} {frames / dt:7.1f} frames/s   "
                  f"frame-send ms p50={p50:6.2f} p99={p99:7.1f} max={mx:7.1f}  {err}")
        print()


if __name__ == "__main__":
    main()

"""Does 'poll for writability, drop the frame if not writable' remove the stalls?

Established facts (device_capacity.py):
  - per-zone writes: ASUS board sustains ~50 frame/s, Corsair Commander Core only ~19.7 frame/s
  - a blind 30 fps target therefore overruns the Corsair, the socket buffer fills, and send()
    blocks for up to 17.9 SECONDS

Strategy under test: keep per-zone writes (they are what actually lights fans/pump), but make
the socket send buffer small and check select-for-write BEFORE each frame. If the socket is not
writable, skip that frame entirely instead of blocking. The effective rate should settle at each
device's real capacity with no long blocks anywhere.
"""
import select, socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051
WINDOW = 12.0
TARGET_FPS = 30.0
SEND_BUF = 8192


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


def enum_socket():
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(30)
    send(s, 0, SET_NAME, b"poll-enum\0")
    return s


def worker(di, name, zones, out):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, SEND_BUF)
    s.settimeout(30)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    time.sleep(0.2)
    send(s, 0, SET_NAME, f"poll-dev{di}\0".encode())

    sent = dropped = attempts = 0
    blocks = []
    t0 = time.perf_counter()
    nxt = t0
    err = ""
    try:
        while time.perf_counter() - t0 < WINDOW:
            attempts += 1
            # backpressure check: is the kernel send buffer ready for another frame?
            writable = select.select([], [s], [], 0)[1]
            if writable:
                w = time.perf_counter()
                for zi, _zn, cnt in zones:
                    buf = bytearray()
                    for k in range(cnt):
                        buf += bytes(((k + sent) % 256, 20, 60, 0))
                    p = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
                    send(s, di, UPDATE_ZONE_LEDS, p)
                blocks.append(time.perf_counter() - w)
                sent += 1
            else:
                dropped += 1

            nxt += 1 / TARGET_FPS
            d = nxt - time.perf_counter()
            if d > 0:
                time.sleep(d)
            else:
                nxt = time.perf_counter()
    except Exception as e:
        err = f"{type(e).__name__}: {e}"
    dt = time.perf_counter() - t0
    out[di] = (name, attempts, sent, dropped, dt, blocks, err)
    try:
        s.close()
    except Exception:
        pass


def main():
    s0 = enum_socket()
    send(s0, 0, REQ_COUNT)
    _, body = recv(s0)
    (count,) = struct.unpack("<I", body[:4])
    devs = []
    for i in range(count):
        send(s0, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s0)
        name, zones = parse(body)
        devs.append((i, name, zones))
    s0.close()

    print(f"target {TARGET_FPS:.0f} fps, SO_SNDBUF={SEND_BUF}, drop-frame on backpressure\n")
    out = {}
    ts = [threading.Thread(target=worker, args=(di, n, z, out)) for di, n, z in devs]
    for t in ts:
        t.start()
    for t in ts:
        t.join()

    for di in sorted(out):
        name, attempts, sent, dropped, dt, blocks, err = out[di]
        blocks.sort()
        n = len(blocks)
        p50 = blocks[n // 2] * 1000 if n else 0
        p99 = blocks[min(int(n * .99), n - 1)] * 1000 if n else 0
        mx = blocks[-1] * 1000 if n else 0
        print(f"dev{di} {name[:28]:28} sent={sent:4d} dropped={dropped:4d} "
              f"-> {sent / dt:5.1f} fps   write-burst ms p50={p50:5.2f} p99={p99:6.2f} max={mx:7.2f}  {err}")


if __name__ == "__main__":
    main()

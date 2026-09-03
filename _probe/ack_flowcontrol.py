"""Can a request/response round-trip act as a per-frame COMPLETION ACK?

Problem (established): the OpenRGB server processes packets from one connection serially, but a
write returns as soon as the kernel buffer accepts it. So a writer can queue far more frames than
a slow device consumes; the backlog grows and eventually one send() blocks for many seconds
(measured: 17.9 s on the Corsair Commander Core).

Idea: after writing a frame, send REQUEST_PROTOCOL_VERSION on the SAME socket and read the reply.
Because the server handles that connection's packets in order, the reply cannot arrive until our
zone writes have been processed. That turns the reply into an exact per-frame completion ack and
makes the writer self-pacing: at most one frame is ever in flight, so no backlog can build.

Success = each device settles at its own real rate with per-frame waits of a few tens of ms and
NO multi-second blocks.
"""
import socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051
WINDOW = 12.0
TARGET_FPS = 30.0


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
    send(s, 0, SET_NAME, b"ack-enum\0")
    return s


def worker(di, name, zones, out):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.settimeout(30)
    # handshake: drain the version reply so later replies line up with our acks
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(30)
    send(s, 0, SET_NAME, f"ack-dev{di}\0".encode())

    delivered = 0
    waits = []
    err = ""
    t0 = time.perf_counter()
    try:
        while time.perf_counter() - t0 < WINDOW:
            w = time.perf_counter()
            for zi, _zn, cnt in zones:
                buf = bytearray()
                for k in range(cnt):
                    buf += bytes(((k + delivered) % 256, 20, 60, 0))
                p = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
                send(s, di, UPDATE_ZONE_LEDS, p)
            # completion ack: the server answers only after processing the writes above
            send(s, 0, REQ_PROTO, struct.pack("<I", 4))
            recv(s)
            waits.append(time.perf_counter() - w)
            delivered += 1
    except Exception as e:
        err = f"{type(e).__name__}: {e}"
    dt = time.perf_counter() - t0
    out[di] = (name, delivered, dt, waits, err)
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

    print("one frame in flight, paced by a protocol round-trip (no artificial delay)\n")
    out = {}
    ts = [threading.Thread(target=worker, args=(di, n, z, out)) for di, n, z in devs]
    for t in ts:
        t.start()
    for t in ts:
        t.join()

    for di in sorted(out):
        name, delivered, dt, waits, err = out[di]
        waits.sort()
        n = len(waits)
        p50 = waits[n // 2] * 1000 if n else 0
        p99 = waits[min(int(n * .99), n - 1)] * 1000 if n else 0
        mx = waits[-1] * 1000 if n else 0
        print(f"dev{di} {name[:28]:28} {delivered / dt:5.1f} fps sustained   "
              f"frame+ack ms p50={p50:6.2f} p99={p99:7.2f} max={mx:8.2f}  {err}")


if __name__ == "__main__":
    main()

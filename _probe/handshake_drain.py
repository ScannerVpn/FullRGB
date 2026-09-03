"""Is an UN-DRAINED handshake reply what kills a write-only SDK socket?

DeviceChannel sends REQUEST_PROTOCOL_VERSION and never reads the 20-byte reply, because the
channel is write-only. The engine then saw dev1's writes time out after 5 s (WSAETIMEDOUT) and
that device fell to ~3 fps, while the earlier probe (which DID read the reply) held 30 fps.

Strategy A reproduces the engine's behaviour (no read), B drains the reply, C skips the version
request entirely. Whichever survives 8 s at 30 fps on BOTH devices is the correct handshake.
"""
import socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051
SECS = 8.0


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


def open_channel(di, mode):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.settimeout(6)                      # mirrors DeviceChannel's 5 s SendTimeout
    if mode in ("nodrain", "drain"):
        send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    if mode == "drain":
        s.settimeout(1.5)
        try:
            recv(s)
        except Exception:
            pass
        s.settimeout(6)
    send(s, 0, SET_NAME, f"probe-{mode}-dev{di}\0".encode())
    return s


def run(mode, devs):
    out = {}

    def worker(di, name, zones):
        try:
            s = open_channel(di, mode)
        except Exception as e:
            out[di] = (name, 0, 0.0, [], f"open failed: {e}")
            return
        t0 = time.perf_counter(); frame = 0; times = []; err = ""
        nxt = t0
        try:
            while time.perf_counter() - t0 < SECS:
                for zi, _zn, cnt in zones:
                    buf = bytearray()
                    for k in range(cnt):
                        buf += bytes(((k + frame) % 256, 25, 70, 0))
                    p = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
                    w = time.perf_counter()
                    send(s, di, UPDATE_ZONE_LEDS, p)
                    times.append(time.perf_counter() - w)
                frame += 1
                nxt += 1 / 30
                d = nxt - time.perf_counter()
                if d > 0:
                    time.sleep(d)
                else:
                    nxt = time.perf_counter()
        except Exception as e:
            err = f"{type(e).__name__}: {e}"
        out[di] = (name, frame, time.perf_counter() - t0, times, err)
        try:
            s.close()
        except Exception:
            pass

    ts = [threading.Thread(target=worker, args=d) for d in devs]
    for t in ts:
        t.start()
    for t in ts:
        t.join()

    print(f"--- {mode} ---")
    for di in sorted(out):
        name, frame, dt, times, err = out[di]
        if times:
            times.sort()
            p99 = times[min(int(len(times) * .99), len(times) - 1)] * 1000
            mx = times[-1] * 1000
        else:
            p99 = mx = 0.0
        flag = "  ERR " + err if err else ""
        rate = frame / dt if dt else 0
        print(f"  dev{di} {name[:26]:26} {rate:5.1f} fps  write p99={p99:6.2f} max={mx:7.1f} ms{flag}")
    print()


def main():
    s0 = socket.create_connection((HOST, PORT), timeout=10)
    s0.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s0, 0, REQ_PROTO, struct.pack("<I", 4))
    s0.settimeout(1.5)
    try:
        recv(s0)
    except Exception:
        pass
    s0.settimeout(20)
    send(s0, 0, SET_NAME, b"probe-enum\0")
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
    print(f"{len(devs)} devices\n")

    for mode in ("nodrain", "drain", "noversion"):
        run(mode, devs)


if __name__ == "__main__":
    main()

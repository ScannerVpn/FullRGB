"""Does one socket per DEVICE remove the multi-hundred-ms write stalls?

FullRGB writes every zone of every device over a single SDK connection. The engine measures
io p99 = 370 ms, i.e. a blocking write occasionally parks the whole render loop, so the
effective rate falls to ~20 fps instead of 30.

Hypothesis: the OpenRGB SDK server serves each CLIENT on one thread and performs the USB/HID
write synchronously, so a slow device blocks every other device on that connection. If true,
one connection per device should let the fast board keep running at 30 fps while the Corsair
controller stalls on its own thread.

Prints, for each strategy, the per-device frame rate and the write-stall distribution.
"""
import socket, struct, threading, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS = 1051
SECS = 8.0


def connect(name):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    s.settimeout(1.5)
    try:
        recv(s)
    except Exception:
        pass
    s.settimeout(30)
    send(s, 0, SET_NAME, name.encode() + b"\0")
    return s


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


def paint(s, di, zones, frame):
    times = []
    for zi, _zn, cnt in zones:
        buf = bytearray()
        for k in range(cnt):
            buf += bytes(((k + frame) % 256, 30, 80, 0))
        p = struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)
        t = time.perf_counter()
        send(s, di, UPDATE_ZONE_LEDS, p)
        times.append(time.perf_counter() - t)
    return times


def report(label, frames, dt, times):
    times.sort()
    n = len(times)
    p = lambda q: times[min(int(n * q), n - 1)] * 1000
    print(f"  {label}: {frames} frames in {dt:.1f}s = {frames / dt:5.1f} fps   "
          f"write ms p50={p(.5):.2f} p90={p(.9):.2f} p99={p(.99):.1f} max={times[-1]*1000:.0f}")


def main():
    s0 = connect("probe-enum")
    send(s0, 0, REQ_COUNT)
    _, body = recv(s0)
    (count,) = struct.unpack("<I", body[:4])
    devs = []
    for i in range(count):
        send(s0, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s0)
        name, zones = parse(body)
        devs.append((i, name, zones))
        print(f"dev {i}: {name} zones={[(z[1], z[2]) for z in zones]}")
    s0.close()
    print()

    # ---- strategy 1: ONE socket, all devices (what FullRGB does today) ----
    print("1) single socket, paced 30 fps")
    s = connect("probe-single")
    t0 = time.perf_counter(); frame = 0; times = []
    nxt = t0
    while time.perf_counter() - t0 < SECS:
        for di, _n, zones in devs:
            times += paint(s, di, zones, frame)
        frame += 1
        nxt += 1 / 30
        d = nxt - time.perf_counter()
        if d > 0:
            time.sleep(d)
        else:
            nxt = time.perf_counter()
    report("all devices", frame, time.perf_counter() - t0, times)
    s.close()
    print()

    # ---- strategy 2: one socket PER DEVICE, independent 30 fps loops ----
    print("2) one socket per device, each paced 30 fps independently")
    results = {}

    def worker(di, name, zones):
        s = connect(f"probe-dev{di}")
        t0 = time.perf_counter(); frame = 0; times = []
        nxt = t0
        while time.perf_counter() - t0 < SECS:
            times += paint(s, di, zones, frame)
            frame += 1
            nxt += 1 / 30
            d = nxt - time.perf_counter()
            if d > 0:
                time.sleep(d)
            else:
                nxt = time.perf_counter()
        results[di] = (name, frame, time.perf_counter() - t0, times)
        s.close()

    threads = [threading.Thread(target=worker, args=d) for d in devs]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    for di in sorted(results):
        name, frame, dt, times = results[di]
        report(name[:28], frame, dt, times)


if __name__ == "__main__":
    main()

"""Does a per-device WRITER THREAD with a latest-frame mailbox remove render stalls?

Established facts:
  device_capacity.py   per-zone capacity: ASUS ~50 frame/s, Corsair ~19.7 frame/s,
                       with single send() calls blocking up to 17.9 s
  poll_backpressure.py select-for-write does NOT predict the stall (max block still 12.6 s),
                       so the block is inside the server/USB path, not the kernel send buffer

Design under test: the render loop never touches the socket. It renders at 30 fps into a
one-slot mailbox (latest frame wins, stale frames are dropped). A writer thread per device
takes whatever is in the mailbox and performs the blocking write. A slow device therefore
receives fewer, newer frames and CANNOT stall the renderer.

Success = render loop holds 30 fps on both devices, and per-device delivered rate equals
that device's real capacity, with no long block inside the render loop.
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
    send(s, 0, SET_NAME, b"mail-enum\0")
    return s


class Mailbox:
    """One slot. put() overwrites (dropping the previous frame); take() blocks for the next."""

    def __init__(self):
        self._cv = threading.Condition()
        self._item = None
        self._closed = False
        self.dropped = 0

    def put(self, item):
        with self._cv:
            if self._item is not None:
                self.dropped += 1
            self._item = item
            self._cv.notify()

    def take(self, timeout=1.0):
        with self._cv:
            if self._item is None:
                self._cv.wait(timeout)
            item, self._item = self._item, None
            return None if self._closed else item

    def close(self):
        with self._cv:
            self._closed = True
            self._cv.notify_all()


def run_device(di, name, zones, out):
    s = socket.create_connection((HOST, PORT), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    s.settimeout(60)
    send(s, 0, REQ_PROTO, struct.pack("<I", 4))
    time.sleep(0.2)
    send(s, 0, SET_NAME, f"mail-dev{di}\0".encode())

    box = Mailbox()
    delivered = [0]
    write_blocks = []
    stop = threading.Event()
    err = [""]

    def writer():
        while not stop.is_set():
            frame = box.take(0.2)
            if frame is None:
                continue
            try:
                w = time.perf_counter()
                for zi, payload in frame:
                    send(s, di, UPDATE_ZONE_LEDS, payload)
                write_blocks.append(time.perf_counter() - w)
                delivered[0] += 1
            except Exception as e:
                err[0] = f"{type(e).__name__}: {e}"
                return

    wt = threading.Thread(target=writer, daemon=True)
    wt.start()

    rendered = 0
    render_blocks = []
    t0 = time.perf_counter()
    nxt = t0
    while time.perf_counter() - t0 < WINDOW:
        w = time.perf_counter()
        frame = []
        for zi, _zn, cnt in zones:
            buf = bytearray()
            for k in range(cnt):
                buf += bytes(((k + rendered) % 256, 20, 60, 0))
            frame.append((zi, struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes(buf)))
        box.put(frame)
        render_blocks.append(time.perf_counter() - w)
        rendered += 1

        nxt += 1 / TARGET_FPS
        d = nxt - time.perf_counter()
        if d > 0:
            time.sleep(d)
        else:
            nxt = time.perf_counter()

    dt = time.perf_counter() - t0
    stop.set()
    box.close()
    wt.join(timeout=2)
    out[di] = (name, rendered, delivered[0], box.dropped, dt, render_blocks, write_blocks, err[0])
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

    print(f"render {TARGET_FPS:.0f} fps into a 1-slot mailbox; writer thread does blocking writes\n")
    out = {}
    ts = [threading.Thread(target=run_device, args=(di, n, z, out)) for di, n, z in devs]
    for t in ts:
        t.start()
    for t in ts:
        t.join()

    for di in sorted(out):
        name, rendered, delivered, dropped, dt, rb, wb, err = out[di]
        rb.sort(); wb.sort()
        rp99 = rb[min(int(len(rb) * .99), len(rb) - 1)] * 1000 if rb else 0
        rmax = rb[-1] * 1000 if rb else 0
        wmax = wb[-1] * 1000 if wb else 0
        print(f"dev{di} {name[:28]:28}")
        print(f"     render {rendered / dt:5.1f} fps (block p99={rp99:.2f} ms, max={rmax:.2f} ms)")
        print(f"     deliver {delivered / dt:5.1f} fps, frames dropped={dropped}, "
              f"writer max block={wmax:.0f} ms  {err}")


if __name__ == "__main__":
    main()

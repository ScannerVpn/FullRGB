"""Why does a Corsair zone write cost 50 ms in one probe and 0.09 ms in another?

Contradiction to resolve (same device, same packet type):
  contention_check.py   zone write + ack: p50 = 49.96 ms, 195/195 samples, rock steady
  queued_or_dropped.py  zone write + ack: median 0.09 ms (one 26.8 ms outlier)

Until this is explained, no conclusion about write cost or granularity is trustworthy.
Hypothesis list: (1) mode state — writes are cheap unless the device is in Direct/custom mode,
(2) warm-up — the first writes after connecting are slow, (3) a whole-device write flips some
state, (4) it depends on which client sent SET_CUSTOM_MODE.

This runs the SAME measurement through a sequence of states on ONE socket and prints the timing
of every phase, so the state that costs 50 ms is identified rather than guessed.
"""
import socket, statistics, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_LEDS, UPDATE_ZONE_LEDS = 1050, 1051
SET_CUSTOM_MODE, UPDATE_MODE = 1100, 1101
N = 25


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
    """Returns name, zones, led_count, modes, active_mode."""
    o = 4
    (_dtype,) = struct.unpack_from("<i", b, o); o += 4
    name, o = rd_str(b, o)
    for _ in range(5):
        _v, o = rd_str(b, o)
    (mcount,) = struct.unpack_from("<H", b, o); o += 2
    (active,) = struct.unpack_from("<i", b, o); o += 4
    modes = []
    for mi in range(mcount):
        mn, o = rd_str(b, o)
        o += 48
        (cc,) = struct.unpack_from("<H", b, o); o += 2 + cc * 4
        modes.append(mn)
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
    return name, [z for z in zones if z[2] > 0], ledc, modes, active


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
    send(s, 0, SET_NAME, f"states-{tag}\0".encode())
    return s


def main():
    s = sock("main")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])
    target = None
    for i in range(count):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, body = recv(s)
        name, zones, ledc, modes, active = parse(body)
        print(f"dev{i}: {name}  activeMode={active} ({modes[active] if 0 <= active < len(modes) else '?'})  "
              f"modes={modes}")
        if "commander" in name.lower() or "corsair" in name.lower():
            target = (i, name, zones, ledc, modes)
    print()
    if target is None:
        print("no Corsair device")
        return
    di, name, zones, ledc, modes = target

    def ack():
        send(s, 0, REQ_PROTO, struct.pack("<I", 4))
        recv(s)

    def zone_write(i):
        zi, _zn, cnt = zones[i % len(zones)]
        send(s, di, UPDATE_ZONE_LEDS,
             struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((i % 256, 25, 80, 0)) * cnt)

    def whole_write(i):
        send(s, di, UPDATE_LEDS,
             struct.pack("<IH", 6 + ledc * 4, ledc) + bytes((70, 20, i % 256, 0)) * ledc)

    def measure(label, writer):
        samples = []
        for i in range(N):
            t = time.perf_counter()
            writer(i)
            ack()
            samples.append((time.perf_counter() - t) * 1000)
        med = statistics.median(samples)
        first5 = " ".join(f"{x:.2f}" for x in samples[:5])
        print(f"  {label:42} median={med:7.2f}  min={min(samples):6.2f} max={max(samples):7.2f}")
        print(f"     first five: {first5}")
        return med

    print("PHASE 1 — as the device currently is")
    measure("zone write + ack", zone_write)
    measure("whole-device write + ack", whole_write)

    print("\nPHASE 2 — after SET_CUSTOM_MODE from this client")
    send(s, di, SET_CUSTOM_MODE)
    ack()
    time.sleep(0.3)
    measure("zone write + ack", zone_write)
    measure("whole-device write + ack", whole_write)

    direct = next((i for i, m in enumerate(modes) if m.lower() == "direct"), -1)
    if direct >= 0:
        print(f"\nPHASE 3 — after explicit UPDATE_MODE to Direct (index {direct})")
        # minimal v4 mode payload: id + name + 12 u32 + colour count
        nm = (modes[direct] + "\0").encode()
        body = struct.pack("<i", direct) + struct.pack("<H", len(nm)) + nm
        body += struct.pack("<IIIIIIIIIII", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) + struct.pack("<H", 0)
        send(s, di, UPDATE_MODE, struct.pack("<I", len(body) + 4) + body)
        ack()
        time.sleep(0.5)
        measure("zone write + ack", zone_write)
        measure("whole-device write + ack", whole_write)

    print("\nPHASE 4 — interleaved: whole-device write then zone write, repeatedly")
    def inter(i):
        whole_write(i)
        zone_write(i)
    measure("whole+zone then ack", inter)

    print("\nPHASE 5 — full frame (all zones) + one ack, like the engine")
    def frame(i):
        for zi, _zn, cnt in zones:
            send(s, di, UPDATE_ZONE_LEDS,
                 struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((i % 256, 25, 80, 0)) * cnt)
    measure("7 zone writes + ack", frame)

    s.close()


if __name__ == "__main__":
    main()

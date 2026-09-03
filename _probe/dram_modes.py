"""Do the ENE DRAM modules accept per-LED colour, and what modes do they expose?

fxtest reported the DIMMs as `direct=False`, meaning FullRGB found no mode literally named
"Direct" on them. Writes are accepted (delivered=21 fps, errors=0), but a device that is not in a
software-controlled mode can ignore them. This dumps every mode with its flags/colour mode, then
paints a known colour and reads the stored value back.
"""
import socket, struct, time

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQ_COUNT, REQ_DATA, REQ_PROTO, SET_NAME = 0, 1, 40, 50
UPDATE_ZONE_LEDS, SET_CUSTOM_MODE = 1051, 1100

MODE_COLORS = {0: "NONE", 1: "PER_LED", 2: "MODE_SPECIFIC", 3: "RANDOM"}


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
    o = 4
    (dtype,) = struct.unpack_from("<i", b, o); o += 4
    name, o = rd_str(b, o)
    for _ in range(5):
        _v, o = rd_str(b, o)
    (mcount,) = struct.unpack_from("<H", b, o); o += 2
    (active,) = struct.unpack_from("<i", b, o); o += 4
    modes = []
    for mi in range(mcount):
        mn, o = rd_str(b, o)
        vals = struct.unpack_from("<12I", b, o); o += 48
        (cc,) = struct.unpack_from("<H", b, o); o += 2 + cc * 4
        # layout after name: value, flags, speed_min, speed_max, bri_min, bri_max,
        #                    colors_min, colors_max, speed, brightness, direction, color_mode
        modes.append((mi, mn, vals[1], vals[11]))
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
    for _ in range(ledc):
        _ln, o = rd_str(b, o)
        o += 4
    (ccount,) = struct.unpack_from("<H", b, o); o += 2
    colors = []
    for _ in range(ccount):
        r, g, bl, _a = struct.unpack_from("<BBBB", b, o); o += 4
        colors.append((r, g, bl))
    return name, dtype, active, modes, [z for z in zones if z[2] > 0], colors


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
    send(s, 0, SET_NAME, b"dram-probe\0")
    send(s, 0, REQ_COUNT)
    _, body = recv(s)
    (count,) = struct.unpack("<I", body[:4])

    def read(i):
        send(s, i, REQ_DATA, struct.pack("<I", 4))
        _, b = recv(s)
        return parse(b)

    def ack():
        send(s, 0, REQ_PROTO, struct.pack("<I", 4))
        recv(s)

    for i in range(count):
        name, dtype, active, modes, zones, colors = read(i)
        if dtype != 1:      # 1 = DRAM
            print(f"dev{i} {name}: type={dtype}, skipping")
            continue
        print(f"\n=== dev{i} {name} (DRAM) ===")
        print(f"active mode index = {active}")
        for mi, mn, flags, cmode in modes:
            mark = " <== ACTIVE" if mi == active else ""
            print(f"  mode {mi}: {mn:18} flags=0x{flags:04X} colorMode={MODE_COLORS.get(cmode, cmode)}{mark}")
        print(f"  zones: {[(z[1], z[2]) for z in zones]}")
        print(f"  stored colours before: {colors[:4]}")

        # paint a distinctive colour on every zone, ack, read back
        for zi, _zn, cnt in zones:
            send(s, i, UPDATE_ZONE_LEDS,
                 struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((203, 17, 89, 0)) * cnt)
        ack()
        time.sleep(0.3)
        _n, _t, _a, _m, _z, colors2 = read(i)
        print(f"  stored colours after zone write: {colors2[:4]}")
        print(f"  applied = {colors2[:1] == [(203, 17, 89)]}")

        # now try SET_CUSTOM_MODE and repeat
        send(s, i, SET_CUSTOM_MODE)
        ack()
        time.sleep(0.3)
        _n, _t, active2, _m, _z, _c = read(i)
        print(f"  active mode after SET_CUSTOM_MODE = {active2}")
        for zi, _zn, cnt in zones:
            send(s, i, UPDATE_ZONE_LEDS,
                 struct.pack("<IiH", 10 + cnt * 4, zi, cnt) + bytes((9, 222, 111, 0)) * cnt)
        ack()
        time.sleep(0.3)
        _n, _t, _a, _m, _z, colors3 = read(i)
        print(f"  stored colours after custom-mode write: {colors3[:4]}")
        print(f"  applied = {colors3[:1] == [(9, 222, 111)]}")

    s.close()


if __name__ == "__main__":
    main()

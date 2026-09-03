"""What does each controller's `location` field actually contain?

The support matrix currently pairs engine controllers with Windows USB devices by comparing NAMES,
which is fragile ("Corsair Commander Core" vs "CORSAIR iCUE COMMANDER Core", and "ASUS Aura
Addressable" vs "AURA LED Controller" does not match at all). OpenRGB reports a `location` string
per controller; for HID devices that is the HID path, which embeds vid/pid. If that holds here, the
matrix can key on VID:PID and stop guessing at names.
"""
import socket
import struct

HOST, PORT = "127.0.0.1", 6742
MAGIC = b"ORGB"
REQUEST_CONTROLLER_COUNT = 0
REQUEST_CONTROLLER_DATA = 1
SET_CLIENT_NAME = 50
REQUEST_PROTOCOL_VERSION = 40


def send(sock, dev, pkt_id, payload=b""):
    sock.sendall(MAGIC + struct.pack("<III", dev, pkt_id, len(payload)) + payload)


def recv_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("closed")
        buf += chunk
    return buf


def recv_pkt(sock):
    hdr = recv_exact(sock, 16)
    assert hdr[:4] == MAGIC, hdr[:4]
    dev, pkt_id, size = struct.unpack("<III", hdr[4:])
    return dev, pkt_id, recv_exact(sock, size) if size else b""


class R:
    def __init__(self, b):
        self.b = b
        self.o = 0

    def u32(self):
        v = struct.unpack_from("<I", self.b, self.o)[0]
        self.o += 4
        return v

    def u16(self):
        v = struct.unpack_from("<H", self.b, self.o)[0]
        self.o += 2
        return v

    def s(self):
        n = self.u16()
        raw = self.b[self.o:self.o + n]
        self.o += n
        return raw.split(b"\x00", 1)[0].decode("utf-8", "replace")


def main():
    s = socket.create_connection((HOST, PORT), timeout=8)
    name = b"probe\x00"
    send(s, 0, SET_CLIENT_NAME, name)
    send(s, 0, REQUEST_PROTOCOL_VERSION, struct.pack("<I", 4))
    _, _, pv = recv_pkt(s)
    proto = struct.unpack("<I", pv)[0]
    send(s, 0, REQUEST_CONTROLLER_COUNT)
    _, _, cc = recv_pkt(s)
    count = struct.unpack("<I", cc)[0]
    print(f"protocol={proto} controllers={count}\n")

    for i in range(count):
        send(s, i, REQUEST_CONTROLLER_DATA, struct.pack("<I", proto))
        _, _, data = recv_pkt(s)
        r = R(data)
        r.u32()          # total size
        dtype = r.u32()
        nm = r.s()
        vendor = r.s()
        desc = r.s()
        ver = r.s()
        serial = r.s()
        location = r.s()
        print(f"[{i}] type={dtype}  name={nm!r}")
        print(f"     vendor={vendor!r} desc={desc!r}")
        print(f"     serial={serial!r}")
        print(f"     location={location!r}")
        print()
    s.close()


if __name__ == "__main__":
    main()

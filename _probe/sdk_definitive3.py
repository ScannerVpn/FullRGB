import subprocess, socket, time, struct, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
try:
    time.sleep(9)
    assert proc.poll() is None, f"DIED rc={proc.returncode}"
    print("server alive at 9s (clean, single instance)")

    s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(5)
    MAGIC = b"ORGB"

    def send_pkt(pid_, payload=b""):
        s.sendall(MAGIC + struct.pack("<I", pid_) + payload)

    def recv_exact(n):
        buf = b""
        while len(buf) < n:
            c = s.recv(n - len(buf))
            if not c: raise EOFError
            buf += c
        return buf

    def recv_pkt():
        hdr = recv_exact(8)
        assert hdr[:4] == MAGIC, hdr[:4]
        pid = struct.unpack("<I", hdr[4:])[0]
        (size,) = struct.unpack("<I", recv_exact(4))
        return pid, recv_exact(size)

    def read_str(d, off):
        (ln,) = struct.unpack_from("<H", d, off); off += 2
        return d[off:off+ln].decode("utf-8", "replace"), off + ln

    send_pkt(2, struct.pack("<I", 0))
    pid, p = recv_pkt()
    print(f"handshake: reply_pkt={pid} protocol={struct.unpack('<I', p)[0]}")

    send_pkt(100)
    pid, p = recv_pkt()
    count = struct.unpack("<I", p)[0]
    print(f"controllers={count}")

    devices = []
    for idx in range(count):
        send_pkt(101, struct.pack("<I", idx))
        pid, d = recv_pkt()
        off = 0
        dev_type, = struct.unpack_from("<I", d, off); off += 4
        name, off = read_str(d, off)
        save = off
        vendor, off2 = read_str(d, save)
        probe, = struct.unpack_from("<I", d, off2)
        if probe > 65535:
            off = off2
        else:
            vendor, off = "", save
        desc, off = read_str(d, off)
        ver, off = read_str(d, off)
        serial, off = read_str(d, off)
        loc, off = read_str(d, off)
        modes, zones, leds, colors = struct.unpack_from("<IIII", d, off)
        devices.append((idx, name, leds))
        print(f"[{idx}] type={dev_type} {name!r} vendor={vendor!r} loc={loc!r} leds={leds} zones={zones} modes={modes}")

    total = sum(l for _, _, l in devices)
    print(f"\ntotal controllable LEDs = {total}")

    for idx, name, leds in devices:
        if leds: send_pkt(1100, struct.pack("<I", idx))
    time.sleep(1)

    def paint(rgb, label):
        for idx, name, leds in devices:
            if leds:
                send_pkt(1050, struct.pack("<II", idx, leds) + bytes(rgb) * leds)
        print(f"painted {label}")
        time.sleep(2)

    paint((255, 0, 0), "RED")
    paint((0, 255, 0), "GREEN")
    paint((0, 0, 255), "BLUE")
    paint((0, 0, 0),   "OFF")

    print(f"server alive after color cycle: {proc.poll() is None}")
    s.close()
finally:
    proc.kill()
    os.system('taskkill /F /IM OpenRGB.exe 2>nul')
print("DEFINITIVE SDK TEST: PASS")

import socket, struct, time, subprocess, os

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(10)

s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(3)

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        c = s.recv(n - len(buf))
        if not c: raise EOFError
        buf += c
    return buf

def send_pkt(dev, ptype, payload=b""):
    s.sendall(b"ORGB" + struct.pack("<II", dev, ptype) + struct.pack("<I", len(payload)) + payload)

def recv_pkt():
    hdr = recv_exact(16)
    dev, ptype = struct.unpack("<II", hdr[4:12])
    size = struct.unpack("<I", hdr[12:16])[0]
    return dev, ptype, recv_exact(size)

send_pkt(0, 40, struct.pack("<I", 4))
try: recv_pkt()
except socket.timeout: pass
send_pkt(0, 50, b"stream2\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# hypothesis: RED+BLACK in same TCP segment got coalesced and broke the server's per-packet reader.
# selftest does: send red, wait 1.5s, send black -> same as timed test which WORKED.
# selftest ALSO does SET_CUSTOM_MODE immediately before. try: SET_CUSTOM_MODE + RED + BLACK all
# with 1.5s gaps but NO drain:
send_pkt(0, 1100)
time.sleep(1.5)
send_pkt(0, 1050, struct.pack("<I", 1) + bytes([255, 0, 0, 0]))
print("RED OK")
time.sleep(1.5)
try:
    send_pkt(0, 1050, struct.pack("<I", 1) + bytes([0, 0, 0, 0]))
    print("BLACK OK")
except OSError as e:
    print("BLACK FAILED:", e)
time.sleep(1)
print("alive:", proc.poll() is None)

# Now THE DIFFERENCE: selftest sends RED to dev0 only ONCE and BLACK to dev0 only once,
# but python exact_seq RED+BLACK also worked... so what breaks?
# Test: RED(dev0), sleep, BLACK(dev0), sleep, then RED(dev0) AGAIN - third update:
try:
    send_pkt(0, 1050, struct.pack("<I", 1) + bytes([0, 0, 255, 0]))
    print("BLUE #3 OK")
except OSError as e:
    print("BLUE #3 FAILED:", e)
time.sleep(1)
print("alive:", proc.poll() is None)

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

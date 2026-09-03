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
send_pkt(0, 50, b"dev1-test\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# test SET_CUSTOM_MODE on dev1 alone (Commander Core, 0 leds, 1 mode 'Direct')
try:
    send_pkt(1, 1100)
    print("SET_CUSTOM_MODE -> dev1: sent OK")
except OSError as e:
    print("dev1 SET_CUSTOM_MODE failed:", e)
time.sleep(0.5)

try:
    send_pkt(1, 1050, struct.pack("<I", 0) + b"")  # 0 leds update on dev1
    print("0-led UPDATE_LEDS -> dev1: sent OK")
except OSError as e:
    print("dev1 0-led update failed:", e)
time.sleep(1)
print("server alive:", proc.poll() is None)

s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

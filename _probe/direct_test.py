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
send_pkt(0, 50, b"direct-test\0")
send_pkt(0, 0)
_, _, p = recv_pkt()
count = struct.unpack("<I", p)[0]

# device 0 (ASUS board, 1 led): set custom mode then paint red then black
send_pkt(0, 1100)          # SET_CUSTOM_MODE, empty payload
time.sleep(0.5)
send_pkt(0, 1050, struct.pack("<I", 1) + bytes([255, 0, 0, 0]))  # 1 led RGBA
print("red sent to dev0")
time.sleep(2)
send_pkt(0, 1050, struct.pack("<I", 1) + bytes([0, 0, 0, 0]))
print("black sent to dev0")
time.sleep(1)

print("server alive:", proc.poll() is None)
s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

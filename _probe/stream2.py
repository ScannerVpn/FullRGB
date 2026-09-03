import socket, struct, time, subprocess, os, threading, math

os.system('taskkill /F /IM OpenRGB.exe 2>nul')
time.sleep(1.5)
EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
proc = subprocess.Popen([EXE, '--server', '--server-port', '6742'])
time.sleep(10)

s = socket.create_connection(("127.0.0.1", 6742), timeout=5); s.settimeout(1)
lock = threading.Lock()
stop = False
server_msgs = []
reader_err = None

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        c = s.recv(n - len(buf))
        if not c: raise EOFError
        buf += c
    return buf

def reader():
    global reader_err
    while not stop:
        try:
            hdr = recv_exact(16)
            dev, ptype = struct.unpack("<II", hdr[4:12])
            size = struct.unpack("<I", hdr[12:16])[0]
            payload = recv_exact(size) if size else b""
            server_msgs.append((dev, ptype, len(payload)))
        except (socket.timeout, TimeoutError):
            continue
        except OSError as e:
            reader_err = e
            break
        except EOFError:
            reader_err = "EOF"
            break

t = threading.Thread(target=reader, daemon=True)
t.start()

def send_pkt(dev, ptype, payload=b""):
    with lock:
        s.sendall(b"ORGB" + struct.pack("<II", dev, ptype) + struct.pack("<I", len(payload)) + payload)

send_pkt(0, 40, struct.pack("<I", 4))
time.sleep(0.5)
send_pkt(0, 50, b"streamer\0")
send_pkt(0, 0)
time.sleep(0.5)

send_pkt(0, 1100)
time.sleep(0.5)
sent = 0
t0 = time.time()
try:
    while time.time() - t0 < 4:
        tt = time.time() - t0
        r = int(127 + 127 * math.sin(tt * 3))
        g = int(127 + 127 * math.sin(tt * 3 + 2))
        b = int(127 + 127 * math.sin(tt * 3 + 4))
        send_pkt(0, 1050, struct.pack("<I", 1) + bytes([r, g, b, 0]))
        sent += 1
        time.sleep(0.033)
    print(f"stream OK: {sent} frames @30fps for 4s")
except OSError as e:
    print(f"stream failed after {sent} frames: {e}")
print(f"server pushed {len(server_msgs)} msgs: {server_msgs[:6]}")
if reader_err: print("reader err:", reader_err)
time.sleep(0.5)
print("alive:", proc.poll() is None)
stop = True
s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

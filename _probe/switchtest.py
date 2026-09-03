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
            reader_err = f"OSError {e}"
            break
        except EOFError:
            reader_err = "EOF (server closed)"
            break

t = threading.Thread(target=reader, daemon=True)
t.start()

def send_pkt(dev, ptype, payload=b""):
    with lock:
        s.sendall(b"ORGB" + struct.pack("<II", dev, ptype) + struct.pack("<I", len(payload)) + payload)

send_pkt(0, 40, struct.pack("<I", 4))
time.sleep(0.5)
send_pkt(0, 50, b"devtest\0")
send_pkt(0, 0)
time.sleep(0.5)

send_pkt(0, 1100)
time.sleep(0.5)
send_pkt(0, 1050, struct.pack("<I", 1) + bytes([255, 0, 0, 0]))
print("dev0 red OK")
time.sleep(1)

# is it DEV-SWITCHING that kills it? try updating dev1 now:
try:
    send_pkt(1, 1100)
    print("dev1 SET_CUSTOM_MODE OK")
    time.sleep(0.5)
    send_pkt(0, 1050, struct.pack("<I", 1) + bytes([0, 0, 0, 0]))
    print("dev0 black OK after touching dev1")
except OSError as e:
    print("FAILED after touching dev1:", e)
time.sleep(1)
print("alive:", proc.poll() is None)
stop = True
s.close(); proc.kill(); os.system('taskkill /F /IM OpenRGB.exe 2>nul')

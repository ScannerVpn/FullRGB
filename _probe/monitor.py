import subprocess, socket, time, os

EXE = r"G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe"
proc = subprocess.Popen([EXE, "--server", "--serverport", "6742"])
start = time.time()
print(f"launched pid={proc.pid}")
alive = True
port_events = []
while time.time() - start < 40:
    if proc.poll() is not None:
        print(f"[{time.time()-start:5.1f}s] PROCESS EXITED code={proc.returncode}")
        alive = False
        break
    # tcp probe
    try:
        s = socket.create_connection(("127.0.0.1", 6742), timeout=0.5)
        s.close()
        if not port_events or port_events[-1][1] is False:
            port_events.append((time.time()-start, True))
            print(f"[{time.time()-start:5.1f}s] port 6742 OPEN")
    except OSError:
        if not port_events:
            port_events.append((time.time()-start, False))
        elif port_events[-1][1] is True:
            port_events.append((time.time()-start, False))
            print(f"[{time.time()-start:5.1f}s] port 6742 CLOSED again")
    time.sleep(1)
if alive:
    print(f"[{time.time()-start:5.1f}s] still alive at end of test, killing")
    proc.kill()
print("port timeline:", port_events)

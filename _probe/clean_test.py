import subprocess, socket, time, os, shutil

EXE = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
CLEAN = r'C:\Users\Sajad\AppData\Local\Temp\or_clean'
shutil.rmtree(CLEAN, ignore_errors=True)
os.makedirs(CLEAN, exist_ok=True)

proc = subprocess.Popen([EXE, '--server', '--serverport', '6742', '--localconfig'],
                        cwd=CLEAN)
start = time.time(); opened_at = None; end = None; last_open = None
while time.time() - start < 30:
    if proc.poll() is not None:
        end = time.time() - start; break
    try:
        s = socket.create_connection(('127.0.0.1', 6742), timeout=0.3); s.close()
        if opened_at is None: opened_at = time.time() - start
        last_open = time.time() - start
    except OSError: pass
    time.sleep(0.2)
alive = proc.poll() is None
print(f'CLEAN-PROFILE: opened_at={opened_at} last_seen_open={last_open} exit_at={end} alive_after_30s={alive}')
if alive:
    print('>>> SERVER STABLE WITH CLEAN PROFILE — root cause was AppData state')
else:
    print('>>> still crashing even with fully clean profile')
proc.kill()
print('files created in clean dir:', os.listdir(CLEAN))

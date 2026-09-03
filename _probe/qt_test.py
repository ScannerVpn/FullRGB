import subprocess, socket, time, os

def run(name, exe, env_extra, args):
    env = os.environ.copy(); env.update(env_extra)
    proc = subprocess.Popen([exe] + args, env=env)
    start = time.time(); opened_at = None; end = None
    while time.time() - start < 25:
        if proc.poll() is not None:
            end = time.time() - start; break
        try:
            s = socket.create_connection(('127.0.0.1', 6742), timeout=0.3); s.close()
            if opened_at is None: opened_at = time.time() - start
        except OSError: pass
        time.sleep(0.2)
    alive = proc.poll() is None
    print(f'{name}: opened_at={opened_at} exit_at={end} alive_after_25s={alive}')
    if alive: print('   >>> SERVER STABLE — SUCCESS')
    proc.kill(); time.sleep(1)

RC = r'G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe'
run('rc3.1 QT_OPENGL=software', RC, {'QT_OPENGL': 'software'}, ['--server', '--serverport', '6742'])
run('rc3.1 software+noeee  ', RC, {'QT_OPENGL': 'software', 'QT_LOGGING_RULES': '*=false'}, ['--server', '--serverport', '6742'])

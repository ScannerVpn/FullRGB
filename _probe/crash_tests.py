import subprocess, socket, time, os, shutil

EXE = r"G:\Ai\RGB Control\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe"
APPDATA_ORG = r"C:\Users\Sajad\AppData\Roaming\OpenRGB"
PLUGINS = os.path.join(APPDATA_ORG, "plugins")
PLUGINS_BAK = os.path.join(APPDATA_ORG, "plugins_bak")

def run_test(name, env_extra, plugins_moved):
    if plugins_moved and os.path.isdir(PLUGINS):
        shutil.move(PLUGINS, PLUGINS_BAK)
    if not plugins_moved and os.path.isdir(PLUGINS_BAK) and not os.path.isdir(PLUGINS):
        shutil.move(PLUGINS_BAK, PLUGINS)
    env = os.environ.copy()
    env.update(env_extra)
    proc = subprocess.Popen([EXE, "--server", "--serverport", "6742"], env=env)
    start = time.time()
    opened_at = closed_at = None
    while time.time() - start < 15:
        if proc.poll() is not None:
            if closed_at is None: closed_at = time.time() - start
            break
        try:
            s = socket.create_connection(("127.0.0.1", 6742), timeout=0.2)
            s.close()
            if opened_at is None: opened_at = time.time() - start
        except OSError:
            if opened_at is not None and closed_at is None:
                closed_at = time.time() - start
        time.sleep(0.15)
    alive = proc.poll() is None
    print(f"{name}: opened_at={opened_at} closed_at={closed_at} alive_after_15s={alive} exit={proc.returncode if not alive else '-'}")
    if alive: proc.kill()
    time.sleep(1)

run_test("test1_no_plugins  ", {}, plugins_moved=True)
run_test("test2_offscreen    ", {"QT_QPA_PLATFORM": "offscreen"}, plugins_moved=False)
run_test("test3_both         ", {"QT_QPA_PLATFORM": "offscreen"}, plugins_moved=True)
# restore plugins for user's normal state
if os.path.isdir(PLUGINS_BAK) and not os.path.isdir(PLUGINS):
    shutil.move(PLUGINS_BAK, PLUGINS)
print("plugins restored:", os.path.isdir(PLUGINS))

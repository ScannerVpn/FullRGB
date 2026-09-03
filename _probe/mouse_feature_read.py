"""READ-ONLY probe of the mouse's vendor HID collection.

Established already: 30FA:1140 "USB GAMING MOUSE" (mfr "INSTANT") exposes
  COL03  usagePage 0xFF00  in=3   out=0  feature=0
  COL04  usagePage 0xFF01  in=0   out=0  feature=8   <-- the control channel
OpenRGB registered no driver for it.

This probe only READS: HidD_GetFeature issues a GET_REPORT control request. It never sends
SET_REPORT/SET_FEATURE, because writing guessed bytes to unknown mouse firmware can corrupt its
configuration flash — not a risk worth taking on the user's hardware to satisfy curiosity.

Goal: find out whether the 8-byte feature report is readable at all, which is the minimum
precondition for any future RGB driver for this device.
"""
import ctypes
from ctypes import wintypes

hid = ctypes.WinDLL("hid")
kernel32 = ctypes.WinDLL("kernel32")

kernel32.CreateFileW.restype = ctypes.c_void_p
kernel32.CreateFileW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                 wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
hid.HidD_GetFeature.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wintypes.ULONG]
hid.HidD_GetFeature.restype = wintypes.BOOL

PATH = (r"\\?\hid#vid_30fa&pid_1140&mi_01&col04#8&d531b7b&0&0003"
        r"#{4d1e55b2-f16f-11cf-88cb-001111000030}")

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
SHARE = 3
OPEN_EXISTING = 3
INVALID = ctypes.c_void_p(-1).value


def try_open(access, label):
    h = kernel32.CreateFileW(PATH, access, SHARE, None, OPEN_EXISTING, 0, None)
    err = ctypes.get_last_error()
    ok = h is not None and h != INVALID and h != 0
    print(f"open({label}): {'OK' if ok else f'FAILED err={err}'}")
    return h if ok else None


def main():
    h = try_open(GENERIC_READ | GENERIC_WRITE, "read|write")
    if h is None:
        h = try_open(GENERIC_READ, "read")
    if h is None:
        h = try_open(0, "metadata-only")
    if h is None:
        print("cannot open the vendor collection at all")
        return

    try:
        # Feature report length is 8 per HIDP_CAPS; byte 0 is the report id.
        for rid in range(0, 8):
            buf = (ctypes.c_ubyte * 8)()
            buf[0] = rid
            ok = hid.HidD_GetFeature(h, ctypes.byref(buf), 8)
            err = ctypes.get_last_error()
            if ok:
                print(f"  feature id {rid}: {' '.join(f'{b:02X}' for b in buf)}")
            else:
                print(f"  feature id {rid}: refused (err={err})")
    finally:
        kernel32.CloseHandle(h)


if __name__ == "__main__":
    main()

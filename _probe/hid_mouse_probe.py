"""Does the mouse (30FA:1140) expose an RGB-capable vendor HID collection, and can we talk to it?

Facts established from Windows:
  VID 30FA PID 1140, bus-reported product string "USB GAMING MOUSE"
  HID collections: MI_00 (mouse), MI_01 COL01 keyboard, COL02 consumer, COL03 "Render ACPI",
                   COL04 vendor-defined, COL05 system controller
  OpenRGB's HID detection registered only the ASUS board and the Corsair hub — no mouse.

30FA is the Sinowealth/generic OEM controller family. OpenRGB has drivers for a handful of these
(usually 258A / 0C45 / 30FA with SPECIFIC PIDs), so the question is whether THIS pid has one.
This probe answers what we can actually verify locally:
  1. enumerate the HID interfaces and print usage page / usage / report lengths
  2. show which collection looks like a vendor control channel (usage page >= 0xFF00)
It does NOT write anything to the device: sending guessed feature reports to an unknown mouse can
brick its firmware, and that is not a risk worth taking on the user's hardware.
"""
import ctypes
from ctypes import wintypes

hid = ctypes.WinDLL("hid")
setupapi = ctypes.WinDLL("setupapi")
kernel32 = ctypes.WinDLL("kernel32")

# Signature discipline matters here: without argtypes/restype, ctypes truncates the 64-bit
# HDEVINFO and the file HANDLE to 32-bit ints, and the enumeration silently returns nothing.
setupapi.SetupDiGetClassDevsW.restype = ctypes.c_void_p
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_void_p,
                                          wintypes.DWORD]
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                                 wintypes.DWORD, ctypes.c_void_p]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                                      ctypes.c_void_p, wintypes.DWORD,
                                                      ctypes.c_void_p, ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [ctypes.c_void_p]
kernel32.CreateFileW.restype = ctypes.c_void_p
kernel32.CreateFileW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                 wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
hid.HidD_GetAttributes.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
hid.HidD_GetProductString.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wintypes.ULONG]
hid.HidD_GetManufacturerString.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wintypes.ULONG]
hid.HidD_GetPreparsedData.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.c_void_p]

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ = 1
FILE_SHARE_WRITE = 2
OPEN_EXISTING = 3
DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wintypes.DWORD), ("Data2", wintypes.WORD),
                ("Data3", wintypes.WORD), ("Data4", ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wintypes.DWORD), ("Reserved", ctypes.POINTER(wintypes.ULONG))]


class SP_DEVICE_INTERFACE_DETAIL_DATA_W(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("DevicePath", wintypes.WCHAR * 512)]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", wintypes.ULONG), ("VendorID", wintypes.USHORT),
                ("ProductID", wintypes.USHORT), ("VersionNumber", wintypes.USHORT)]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [
        ("Usage", wintypes.USHORT), ("UsagePage", wintypes.USHORT),
        ("InputReportByteLength", wintypes.USHORT),
        ("OutputReportByteLength", wintypes.USHORT),
        ("FeatureReportByteLength", wintypes.USHORT),
        ("Reserved", wintypes.USHORT * 17),
        ("NumberLinkCollectionNodes", wintypes.USHORT),
        ("NumberInputButtonCaps", wintypes.USHORT),
        ("NumberInputValueCaps", wintypes.USHORT),
        ("NumberInputDataIndices", wintypes.USHORT),
        ("NumberOutputButtonCaps", wintypes.USHORT),
        ("NumberOutputValueCaps", wintypes.USHORT),
        ("NumberOutputDataIndices", wintypes.USHORT),
        ("NumberFeatureButtonCaps", wintypes.USHORT),
        ("NumberFeatureValueCaps", wintypes.USHORT),
        ("NumberFeatureDataIndices", wintypes.USHORT),
    ]


def hid_guid():
    g = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(g))
    return g


def enumerate_hid():
    g = hid_guid()
    h = setupapi.SetupDiGetClassDevsW(ctypes.byref(g), None, None,
                                      DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if h == INVALID_HANDLE_VALUE:
        return
    i = 0
    while True:
        did = SP_DEVICE_INTERFACE_DATA()
        did.cbSize = ctypes.sizeof(did)
        if not setupapi.SetupDiEnumDeviceInterfaces(h, None, ctypes.byref(g), i, ctypes.byref(did)):
            break
        need = wintypes.DWORD()
        setupapi.SetupDiGetDeviceInterfaceDetailW(h, ctypes.byref(did), None, 0,
                                                  ctypes.byref(need), None)
        detail = SP_DEVICE_INTERFACE_DETAIL_DATA_W()
        detail.cbSize = 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6
        if setupapi.SetupDiGetDeviceInterfaceDetailW(h, ctypes.byref(did), ctypes.byref(detail),
                                                    ctypes.sizeof(detail), ctypes.byref(need), None):
            yield detail.DevicePath
        i += 1
    setupapi.SetupDiDestroyDeviceInfoList(h)


def open_path(path, write=False):
    access = GENERIC_READ | (GENERIC_WRITE if write else 0)
    h = kernel32.CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             None, OPEN_EXISTING, 0, None)
    # Mice/keyboards are opened exclusively by Windows for input; a read/write open fails with
    # ERROR_ACCESS_DENIED (5). Retry with zero access — HidD_* metadata still works.
    if h is None or h == INVALID_HANDLE_VALUE:
        h = kernel32.CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                 None, OPEN_EXISTING, 0, None)
    return h


def wstr(fn, handle, n=256):
    buf = ctypes.create_unicode_buffer(n)
    if fn(handle, buf, ctypes.sizeof(buf)):
        return buf.value
    return ""


def main():
    targets = {(0x30FA, 0x1140): "mouse", (0x2A7A, 0x939F): "keyboard",
               (0x1B1C, 0x0C32): "commander core (reference: DOES work)",
               (0x0B05, 0x18F3): "aura controller (reference: DOES work)"}
    seen = 0
    for path in enumerate_hid():
        h = open_path(path)
        if h == INVALID_HANDLE_VALUE or h == 0:
            continue
        try:
            attrs = HIDD_ATTRIBUTES()
            attrs.Size = ctypes.sizeof(attrs)
            if not hid.HidD_GetAttributes(h, ctypes.byref(attrs)):
                continue
            key = (attrs.VendorID, attrs.ProductID)
            if key not in targets:
                continue
            seen += 1

            product = wstr(hid.HidD_GetProductString, h)
            manuf = wstr(hid.HidD_GetManufacturerString, h)

            pp = ctypes.c_void_p()
            caps = HIDP_CAPS()
            if hid.HidD_GetPreparsedData(h, ctypes.byref(pp)):
                hid.HidP_GetCaps(pp, ctypes.byref(caps))
                hid.HidD_FreePreparsedData(pp)

            vendor_page = caps.UsagePage >= 0xFF00
            print(f"{attrs.VendorID:04X}:{attrs.ProductID:04X}  {targets[key]}")
            print(f"   path      {path[:96]}")
            print(f"   product   '{product}'   mfr '{manuf}'")
            print(f"   usagePage 0x{caps.UsagePage:04X} usage 0x{caps.Usage:04X}"
                  f"{'   <== VENDOR-DEFINED' if vendor_page else ''}")
            print(f"   reports   in={caps.InputReportByteLength} "
                  f"out={caps.OutputReportByteLength} feature={caps.FeatureReportByteLength}")
            print()
        finally:
            kernel32.CloseHandle(h)
    if seen == 0:
        print("no target HID interfaces opened")


if __name__ == "__main__":
    main()

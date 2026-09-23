using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FullRGB.Hid;

/// <summary>
/// The smallest safe Win32 HID surface: enumerate HID interface paths, match one by VID/PID,
/// open it, read a feature report (probe) and — only behind the safety gates — write one
/// output report (paint). Everything else a HID driver would need is deliberately NOT here.
///
/// Why interface paths: a device exposes several HID collections; the protocol file names the
/// one it targets via usagePage/usage, so we match the collection rather than blasting every
/// handle the device offers. Match is best-effort: when usage data cannot be read, the FIRST
/// collection of the device is used (single-collection OEM mice/keeps — the common case).
/// </summary>
public static class HidBridge
{
    private const int FILE_SHARE_READ = 1;
    private const int FILE_SHARE_WRITE = 2;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const int OPEN_EXISTING = 3;
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    // NOTE: deliberately NOT readonly — it is passed by ref to SetupDi* (CS0199 forbids
    // readonly fields as ref arguments). It is never mutated after construction.
    private static Guid GuidDevinterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA ifaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devInfo,
        ref SP_DEVICE_INTERFACE_DATA ifaceData, IntPtr detailBuffer, int detailSize,
        out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, int share, IntPtr security,
        int disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr handle, out HIDD_ATTRIBUTES attrs);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(IntPtr handle, byte[] report, int reportLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(IntPtr handle, byte[] report, int reportLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public ushort Reserved_1a, Reserved_1c, Reserved_1e, Reserved_20;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    public sealed class HidCollection
    {
        public string Path { get; init; } = "";
        public ushort Vid { get; init; }
        public ushort Pid { get; init; }
        public ushort UsagePage { get; init; }
        public ushort Usage { get; init; }
        public ushort FeatureLength { get; init; }
        public ushort OutputLength { get; init; }
        public string VidPid => $"{Vid:X4}:{Pid:X4}";
    }

    /// <summary>Every present HID collection (path + attributes). No elevation needed.</summary>
    public static List<HidCollection> Enumerate()
    {
        var list = new List<HidCollection>();
        IntPtr set = SetupDiGetClassDevs(ref GuidDevinterfaceHid, IntPtr.Zero, IntPtr.Zero,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GuidDevinterfaceHid, i, ref iface)) break;

                // size query pass, then the real pass
                SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out int need, IntPtr.Zero);
                if (need <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(need);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize (int) + string. On 64-bit, cbSize = 8.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, need, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0) continue;

                    var col = Inspect(path);
                    if (col is not null) list.Add(col);
                }
                finally { Marshal.FreeHGlobal(detail); }
                if (list.Count > 256) break;   // paranoia cap
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static HidCollection? Inspect(string path)
    {
        IntPtr h = Open(path, GENERIC_READ);
        if (h == IntPtr.Zero) h = Open(path, 0);   // exclusive keyboards: try attributes without read access
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!HidD_GetAttributes(h, out var attrs)) return null;
            ushort up = 0, us = 0, fl = 0, ol = 0;
            if (HidD_GetPreparsedData(h, out var preparsed))
            {
                try
                {
                    if (HidD_GetCaps(preparsed, out var caps))
                    {
                        up = caps.UsagePage; us = caps.Usage;
                        fl = caps.FeatureReportByteLength; ol = caps.OutputReportByteLength;
                    }
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }
            return new HidCollection
            {
                Path = path, Vid = attrs.VendorID, Pid = attrs.ProductID,
                UsagePage = up, Usage = us, FeatureLength = fl, OutputLength = ol,
            };
        }
        finally { CloseHandle(h); }
    }

    private static IntPtr Open(string path, uint access)
        => CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>
    /// Read-only probe of one collection: GET FEATURE with the declared report id/length.
    /// Returns the raw bytes (or null + error). This is the traffic class detection already uses.
    /// </summary>
    public static (byte[]? data, string error) Probe(HidCollection col, int reportId, int length)
    {
        IntPtr h = Open(col.Path, GENERIC_READ);
        if (h == IntPtr.Zero) return (null, Win32Error("opening the device read-only failed"));
        try
        {
            int cap = Math.Max(length, col.FeatureLength);
            var buf = new byte[Math.Max(2, cap)];
            if (reportId > 0) buf[0] = (byte)reportId;
            if (!HidD_GetFeature(h, buf, buf.Length))
                return (null, Win32Error("reading the feature report failed"));
            return (buf, "");
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// ONE guarded paint frame (solid colour) via SET FEATURE. Caller must have ensured
    /// protocol.CanWrite AND the user's double confirmation — this method only enforces the
    /// mechanical bounds (length cap, single collection, single frame).
    /// </summary>
    public static (bool ok, string error) PaintSolid(HidCollection col, HidProtocolFile proto, System.Drawing.Color color)
    {
        if (proto.Paint is not { } paint) return (false, "protocol has no paint section");
        if (!paint.Validate(out var verr)) return (false, verr);
        if (!proto.CanWrite) return (false, "experimental writes are disabled");
        if (paint.Length > 64) return (false, "report too large");

        // The declared length INCLUDES the report-id byte (HID convention): byte 0 is the id
        // (0 when the protocol declares none), then prefix + colour + tail + zero padding.
        byte[] buf = new byte[paint.Length];
        int off = 0;
        buf[off++] = (byte)paint.ReportId;
        foreach (var b in HexBytes(paint.Prefix)) buf[off++] = b;

        byte r = color.R, g = color.G, b2 = color.B;
        if (paint.Order == "BGR") (r, b2) = (b2, r);
        for (int i = 0; i < paint.RgbSlots; i++)
        {
            buf[off++] = r; buf[off++] = g; buf[off++] = b2;
        }
        foreach (var b in HexBytes(paint.Tail)) buf[off++] = b;
        // remaining bytes stay zero — that IS the declared tail padding

        IntPtr h = Open(col.Path, GENERIC_READ | GENERIC_WRITE);
        if (h == IntPtr.Zero) return (false, Win32Error("opening the device for write failed"));
        try
        {
            if (!HidD_SetFeature(h, buf, buf.Length))
                return (false, Win32Error("writing the report failed"));
            return (true, "");
        }
        finally { CloseHandle(h); }
    }

    private static byte[] HexBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var parts = hex.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<byte>(parts.Length);
        foreach (var p in parts)
            if (byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out var b)) list.Add(b);
        return list.ToArray();
    }

    private static string Win32Error(string what) => $"{what} (Win32 error {Marshal.GetLastWin32Error()})";
}

using System.Runtime.InteropServices;
using System.Text;

namespace FullRGB.Diag;

/// <summary>One USB/HID device as Windows sees it.</summary>
public sealed class UsbDevice
{
    public string InstanceId { get; init; } = "";
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }
    /// <summary>The product string the DEVICE reports (e.g. "USB GAMING MOUSE"), not the driver name.</summary>
    public string ProductName { get; init; } = "";
    public string DeviceClass { get; init; } = "";

    public string VidPid => $"{Vid:X4}:{Pid:X4}";

    /// <summary>
    /// Best available human label. `DeviceDesc` is usually the generic driver name
    /// ("USB Input Device"), so the bus-reported product string wins when present.
    /// </summary>
    public string Label => string.IsNullOrWhiteSpace(ProductName) ? $"USB {VidPid}" : ProductName.Trim();
}

/// <summary>
/// Enumerates present USB/HID devices so FullRGB can tell the user WHY a peripheral is missing
/// from the device list, instead of leaving them guessing.
///
/// Runs as a normal user: SetupAPI enumeration and the bus-reported product string need no
/// elevation (verified on this rig — the same data the Device Manager shows).
/// </summary>
public static class UsbScan
{
    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_ALLCLASSES = 0x04;
    private const int SPDRP_CLASS = 0x00000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // DEVPKEY_Device_BusReportedDeviceDesc — the string the device itself reports.
    // NOT static readonly: SetupDiGetDevicePropertyW takes it by ref, which a readonly field
    // cannot satisfy outside a static constructor.
    private static DEVPROPKEY BusReportedDeviceDesc = new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        pid = 4,
    };

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devInfo, ref SP_DEVINFO_DATA data,
        StringBuilder buffer, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr devInfo, ref SP_DEVINFO_DATA data,
        ref DEVPROPKEY propKey, out uint propType, byte[]? buffer, int bufferSize,
        out int requiredSize, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr devInfo, ref SP_DEVINFO_DATA data,
        int property, out uint propType, byte[]? buffer, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    /// <summary>
    /// Every present device whose instance id carries a VID/PID, deduplicated per VID:PID
    /// (a mouse shows up as 5+ HID collections; the user cares about one entry).
    /// </summary>
    public static List<UsbDevice> Scan()
    {
        var found = new Dictionary<string, UsbDevice>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new List<UsbDevice>();

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var sb = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out _)) continue;
                string id = sb.ToString();
                if (!TryParseVidPid(id, out ushort vid, out ushort pid)) continue;

                string key = $"{vid:X4}:{pid:X4}";
                string product = GetStringProperty(set, ref data, ref BusReportedDeviceDesc);
                string cls = GetRegString(set, ref data, SPDRP_CLASS);

                // Keep the richest record per VID:PID: prefer one that has a product name, and
                // prefer a meaningful class over "USB".
                if (found.TryGetValue(key, out var existing))
                {
                    bool better = (string.IsNullOrWhiteSpace(existing.ProductName) && !string.IsNullOrWhiteSpace(product))
                               || (existing.DeviceClass is "USB" or "" && cls is not ("USB" or ""));
                    if (!better) continue;
                    if (string.IsNullOrWhiteSpace(product)) product = existing.ProductName;
                    if (cls is "USB" or "") cls = existing.DeviceClass;
                }

                found[key] = new UsbDevice
                {
                    InstanceId = id,
                    Vid = vid,
                    Pid = pid,
                    ProductName = product,
                    DeviceClass = cls,
                };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return found.Values.OrderBy(d => d.DeviceClass).ThenBy(d => d.Label).ToList();
    }

    /// <summary>Parses VID_xxxx&amp;PID_xxxx out of a device instance id.</summary>
    internal static bool TryParseVidPid(string instanceId, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        int v = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int p = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (v < 0 || p < 0 || v + 8 > instanceId.Length || p + 8 > instanceId.Length) return false;
        return ushort.TryParse(instanceId.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber,
                               null, out vid)
            && ushort.TryParse(instanceId.AsSpan(p + 4, 4), System.Globalization.NumberStyles.HexNumber,
                               null, out pid);
    }

    private static string GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref data, ref key, out _, null, 0, out int need, 0);
        if (need <= 0) return "";
        var buf = new byte[need];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, buf.Length, out _, 0)) return "";
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    private static string GetRegString(IntPtr set, ref SP_DEVINFO_DATA data, int property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out int need);
        if (need <= 0) return "";
        var buf = new byte[need];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buf, buf.Length, out _)) return "";
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }
}

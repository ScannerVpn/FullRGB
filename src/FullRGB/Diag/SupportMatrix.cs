using FullRGB.SDK;

namespace FullRGB.Diag;

/// <summary>Why a peripheral is (or is not) controllable.</summary>
public enum SupportState
{
    /// <summary>FullRGB is driving it right now.</summary>
    Controlled,
    /// <summary>Lighting exists but needs one setup step (currently: SMBus access for RGB RAM).</summary>
    NeedsElevation,
    /// <summary>Present, but the engine has no lighting driver for this exact model.</summary>
    Unsupported,
    /// <summary>Present; support is UNKNOWN because the engine is not connected.</summary>
    Unknown,
    /// <summary>Not RGB hardware at all (hubs, audio, Bluetooth radios…). Not shown.</summary>
    NotLighting,
}

public sealed class PeripheralReport
{
    public string Label { get; init; } = "";
    public string VidPid { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public SupportState State { get; init; }
    /// <summary>One short sentence the user can act on. Already localized.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Explains the gap between "devices plugged into this PC" and "devices FullRGB can light up".
///
/// WHY THIS EXISTS: the user asked why some of their hardware is missing from the list. Answering
/// "it isn't supported" is only honest if we can name WHICH device and WHY.
///
/// Matching is done on VID:PID, not on names. Verified against this rig's engine output: every USB
/// controller reports `location` as `HID: \\?\HID#VID_xxxx&PID_xxxx#...`, and SMBus devices report
/// `I2C: i801, address 0x71`. Name matching was tried first and failed on real data — the engine
/// says "ASUS ROG MAXIMUS Z790 DARK HERO" where Windows says "AURA LED Controller".
/// </summary>
public static class SupportMatrix
{
    /// <summary>
    /// Vendors whose peripherals commonly have RGB. Used ONLY to decide whether a device is worth
    /// listing and to name the maker — never to claim support.
    /// </summary>
    private static readonly Dictionary<ushort, string> RgbVendors = new()
    {
        [0x1B1C] = "Corsair",
        [0x1E7D] = "Roccat",
        [0x1532] = "Razer",
        [0x046D] = "Logitech",
        [0x0B05] = "ASUS",
        [0x1044] = "Gigabyte / Aorus",
        [0x1462] = "MSI",
        [0x2516] = "Cooler Master",
        [0x0C45] = "Sonix (OEM keyboards/mice)",
        [0x30FA] = "Instant / Sinowealth (OEM gaming)",
        [0x2A7A] = "CASUE (OEM keyboard)",
        [0x258A] = "Sino Wealth",
        [0x3151] = "Keychron",
        [0x1038] = "SteelSeries",
        [0x1E71] = "NZXT",
        [0x3633] = "HyperX",
        [0x0951] = "Kingston / HyperX",
        [0x264A] = "Thermaltake",
        [0x1A2C] = "Semico (OEM keyboard)",
    };

    /// <summary>Device classes that can carry lighting.</summary>
    private static readonly HashSet<string> LightingClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mouse", "Keyboard", "HIDClass", "USB", "System",
    };

    /// <summary>Classes that are never lighting, even under a known RGB vendor id.</summary>
    private static readonly HashSet<string> NeverLighting = new(StringComparer.OrdinalIgnoreCase)
    {
        "USBDevice", "DiskDrive", "WPD", "Volume", "Net", "Bluetooth", "Camera", "Image",
        "Printer", "SmartCardReader", "Ports", "AudioEndpoint", "MEDIA", "Media",
    };

    /// <summary>
    /// Builds the report. <paramref name="controllers"/> is what the engine found;
    /// <paramref name="smbusFailed"/> comes from the engine log (PawnIO/permission denied).
    /// <paramref name="engineConnected"/> false means we have NO controller list at all, so the
    /// report must not claim anything about driver support — only that nothing is known yet.
    /// </summary>
    public static List<PeripheralReport> Build(IEnumerable<RgbController> controllers, bool smbusFailed,
                                               bool engineConnected = true)
    {
        var controlled = controllers.ToList();

        // VID:PID -> engine controller, for the USB/HID ones.
        var byVidPid = new Dictionary<string, RgbController>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in controlled)
        {
            var key = VidPidFromLocation(c.Location);
            if (key is not null) byVidPid[key] = c;
        }

        var list = new List<PeripheralReport>();

        // --- SMBus devices first: they are not USB, so a USB scan can never see them. ---
        var dram = controlled.Where(c => c.Kind == RgbDeviceType.DRAM).ToList();
        foreach (var d in dram)
            list.Add(new PeripheralReport
            {
                Label = d.Name,
                VidPid = "SMBus",
                DeviceClass = "RAM",
                State = SupportState.Controlled,
                Reason = L10n.T("sup.controlled", d.LedCount),
            });

        if (dram.Count == 0 && smbusFailed)
            list.Add(new PeripheralReport
            {
                Label = L10n.T("sup.ramLabel"),
                VidPid = "SMBus",
                DeviceClass = "RAM",
                State = SupportState.NeedsElevation,
                Reason = L10n.T("sup.ramNeedsTask"),
            });

        // --- Everything Windows can see on USB. ---
        foreach (var dev in UsbScan.Scan())
        {
            bool knownVendor = RgbVendors.TryGetValue(dev.Vid, out var vendor);

            if (byVidPid.TryGetValue(dev.VidPid, out var match))
            {
                list.Add(new PeripheralReport
                {
                    Label = match.Name,
                    VidPid = dev.VidPid,
                    DeviceClass = dev.DeviceClass,
                    State = SupportState.Controlled,
                    Reason = L10n.T("sup.controlled", match.LedCount),
                });
                continue;
            }

            bool couldLight = LightingClasses.Contains(dev.DeviceClass)
                              && !NeverLighting.Contains(dev.DeviceClass);
            if (!couldLight || (!knownVendor && !LooksLikePeripheral(dev)))
            {
                list.Add(new PeripheralReport
                {
                    Label = dev.Label,
                    VidPid = dev.VidPid,
                    DeviceClass = dev.DeviceClass,
                    State = SupportState.NotLighting,
                    Reason = "",
                });
                continue;
            }

            list.Add(new PeripheralReport
            {
                Label = dev.Label,
                VidPid = dev.VidPid,
                DeviceClass = dev.DeviceClass,
                // With no engine connection we know nothing about drivers. Saying "not
                // controllable" here would be a lie about hardware that works fine — which is
                // exactly what the headless --uishot pass exposed (it labelled the ASUS board
                // and the Corsair hub unsupported).
                State = engineConnected ? SupportState.Unsupported : SupportState.Unknown,
                Reason = engineConnected
                    ? (knownVendor ? L10n.T("sup.noDriverVendor", vendor!) : L10n.T("sup.noDriver"))
                    : L10n.T("sup.engineOffline"),
            });
        }

        // Controllers the engine drives that no USB row claimed (non-HID buses other than DRAM,
        // e.g. an I2C GPU). Never silently drop them: the counts must add up.
        foreach (var c in controlled)
        {
            if (c.Kind == RgbDeviceType.DRAM) continue;
            var key = VidPidFromLocation(c.Location);
            if (key is not null && list.Any(r => r.VidPid.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                 && r.State == SupportState.Controlled)) continue;
            if (key is null && list.Any(r => r.Label == c.Name)) continue;

            list.Add(new PeripheralReport
            {
                Label = c.Name,
                VidPid = key ?? BusLabel(c.Location),
                DeviceClass = c.Kind.ToString(),
                State = SupportState.Controlled,
                Reason = L10n.T("sup.controlled", c.LedCount),
            });
        }

        return list;
    }

    /// <summary>
    /// Pulls VID_xxxx&amp;PID_xxxx out of an engine `location` string, e.g.
    /// <c>HID: \\?\HID#VID_1B1C&amp;PID_0C32&amp;MI_00#8&amp;376e446a&amp;0&amp;0000#{...}</c>.
    /// Returns null for non-USB buses (I2C/SMBus, "Unknown", empty).
    /// </summary>
    internal static string? VidPidFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        if (!UsbScan.TryParseVidPid(location, out ushort vid, out ushort pid)) return null;
        return $"{vid:X4}:{pid:X4}";
    }

    /// <summary>Short bus tag for a controller with no VID/PID, used in place of one.</summary>
    private static string BusLabel(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "—";
        int colon = location.IndexOf(':');
        return colon > 0 ? location[..colon].Trim() : location.Trim();
    }

    /// <summary>Anything with a mouse/keyboard class is worth listing even from an unknown vendor.</summary>
    private static bool LooksLikePeripheral(UsbDevice d)
        => d.DeviceClass is "Mouse" or "Keyboard"
        || (d.DeviceClass == "HIDClass" && !string.IsNullOrWhiteSpace(d.ProductName));
}

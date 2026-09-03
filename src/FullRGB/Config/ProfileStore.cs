using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullRGB.Effects;
using FullRGB.SDK;

namespace FullRGB.Config;

/// <summary>Per-device colour calibration: different LED chips render the same RGB triple differently.</summary>
public sealed class Calibration
{
    public double RGain { get; set; } = 1.0;
    public double GGain { get; set; } = 1.0;
    public double BGain { get; set; } = 1.0;
    public double Gamma { get; set; } = 1.0;   // 1.0 = linear passthrough

    public bool IsIdentity => Math.Abs(RGain - 1) < 0.001 && Math.Abs(GGain - 1) < 0.001
                           && Math.Abs(BGain - 1) < 0.001 && Math.Abs(Gamma - 1) < 0.001;

    public Calibration Clone() => new()
    { RGain = RGain, GGain = GGain, BGain = BGain, Gamma = Gamma };

    /// <summary>Applies gamma then per-channel gain to an RGB buffer, in place.</summary>
    public void Apply(byte[] rgb)
    {
        if (IsIdentity) return;
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            rgb[i]     = Map(rgb[i], RGain);
            rgb[i + 1] = Map(rgb[i + 1], GGain);
            rgb[i + 2] = Map(rgb[i + 2], BGain);
        }
    }

    private byte Map(byte v, double gain)
    {
        double x = v / 255.0;
        if (Math.Abs(Gamma - 1) > 0.001) x = Math.Pow(x, Gamma);
        return (byte)Math.Clamp(Math.Round(x * gain * 255.0), 0, 255);
    }
}

/// <summary>App profile: global effect + per-device/per-zone overrides + per-zone LED counts.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "Default";
    public EffectDef GlobalEffect { get; set; } = new();

    /// <summary>Per-device effect override, keyed by RgbController.Key (name@location).</summary>
    public Dictionary<string, EffectDef> DeviceOverrides { get; set; } = new();

    /// <summary>Per-zone effect override, keyed by "deviceKey|zoneIndex". Beats the device override.</summary>
    public Dictionary<string, EffectDef> ZoneOverrides { get; set; } = new();

    /// <summary>Devices left in their hardware lighting mode, keyed by RgbController.Key.</summary>
    public List<string> ExcludedDevices { get; set; } = new();

    /// <summary>User-declared real LED count per zone, keyed by "deviceKey|zoneIndex".</summary>
    public Dictionary<string, int> ZoneSizes { get; set; } = new();

    /// <summary>Per-device colour correction, keyed by RgbController.Key.</summary>
    public Dictionary<string, Calibration> Calibrations { get; set; } = new();

    public static string ZoneKey(RgbController dev, RgbZone zone) => $"{dev.Key}|{zone.Index}";

    public bool IsExcluded(RgbController dev) =>
        ExcludedDevices.Contains(dev.Key) ||
        ExcludedDevices.Any(x => x.Equals(dev.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Effect for a whole device (used by the editor when no zone is selected).</summary>
    public EffectDef EffectFor(RgbController dev)
    {
        if (DeviceOverrides.TryGetValue(dev.Key, out var byKey)) return byKey;
        if (DeviceOverrides.TryGetValue(dev.Name, out var byName)) return byName;
        return GlobalEffect;
    }

    /// <summary>Effect actually painted on one zone: zone override &gt; device override &gt; global.</summary>
    public EffectDef EffectFor(RgbController dev, RgbZone zone)
        => ZoneOverrides.TryGetValue(ZoneKey(dev, zone), out var z) ? z : EffectFor(dev);

    public bool HasZoneOverride(RgbController dev, RgbZone zone)
        => ZoneOverrides.ContainsKey(ZoneKey(dev, zone));

    public Calibration CalibrationFor(RgbController dev) =>
        Calibrations.TryGetValue(dev.Key, out var c) ? c : new Calibration();

    /// <summary>Desired LED count for a zone: user value if set, else the zone's max.</summary>
    public int ZoneSize(RgbController dev, RgbZone zone) =>
        ZoneSizes.TryGetValue(ZoneKey(dev, zone), out var n) && n > 0
            ? (int)Math.Clamp((uint)n, zone.LedsMin, Math.Min(zone.LedsMax, int.MaxValue))
            : (int)zone.LedsMax;

    /// <summary>Drops overrides/sizes that no longer match any present device (keeps files from bloating).</summary>
    public void PruneTo(IEnumerable<RgbController> devices)
    {
        var keys = devices.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0) return;
        foreach (var k in DeviceOverrides.Keys.Where(k => !keys.Contains(k)).ToList())
            DeviceOverrides.Remove(k);
        foreach (var k in Calibrations.Keys.Where(k => !keys.Contains(k)).ToList())
            Calibrations.Remove(k);
        foreach (var k in ZoneOverrides.Keys.Where(k => !keys.Contains(DevicePart(k))).ToList())
            ZoneOverrides.Remove(k);
        foreach (var k in ZoneSizes.Keys.Where(k => !keys.Contains(DevicePart(k))).ToList())
            ZoneSizes.Remove(k);

        static string DevicePart(string zoneKey)
        {
            int i = zoneKey.LastIndexOf('|');
            return i < 0 ? zoneKey : zoneKey[..i];
        }
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "en"; // en | fa
    public int ServerPort { get; set; } = 6742;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }

    /// <summary>UI accent colour (hex). Also used for the brand ring and the toggles.</summary>
    public string AccentHex { get; set; } = "#00E5FF";

    /// <summary>Restore the last effect and start painting as soon as the app launches.</summary>
    public bool AutoStartEffects { get; set; } = true;

    /// <summary>When true the bundled OpenRGB engine is killed when FullRGB truly exits
    /// (tray → Exit). When false (default) it stays alive so the lights keep their last
    /// state after the UI is gone. False keeps the current behaviour; true fixes the
    /// \"must kill from Task Manager\" complaint without forcing UAC on every user.</summary>
    public bool CloseEngineOnExit { get; set; } = false;

    public string ActiveProfile { get; set; } = "Default";
    public List<Profile> Profiles { get; set; } = new() { new() };

    /// <summary>Repairs anything a hand-edited or older settings file could get wrong.</summary>
    public AppSettings Normalized()
    {
        if (Profiles.Count == 0) Profiles.Add(new Profile());
        // duplicate or blank profile names break the ComboBox + tray submenu
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Profiles.Count; i++)
        {
            var p = Profiles[i];
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"Profile {i + 1}";
            var baseName = p.Name;
            int n = 2;
            while (!seen.Add(p.Name)) p.Name = $"{baseName} ({n++})";
            p.GlobalEffect ??= new EffectDef();
            p.DeviceOverrides ??= new();
            p.ZoneOverrides ??= new();
            p.ExcludedDevices ??= new();
            p.ZoneSizes ??= new();
            p.Calibrations ??= new();
        }
        if (!Profiles.Any(p => p.Name.Equals(ActiveProfile, StringComparison.OrdinalIgnoreCase)))
            ActiveProfile = Profiles[0].Name;
        if (Language != "fa") Language = "en";
        if (ServerPort is < 1 or > 65535) ServerPort = 6742;
        if (!IsHexColor(AccentHex)) AccentHex = "#00E5FF";
        return this;
    }

    public static bool IsHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        return (s.Length == 6 || s.Length == 3)
            && s.All(c => Uri.IsHexDigit(c));
    }
}

public static class ProfileStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB");

    private static string SettingsPath => Path.Combine(Dir, "settings.json");
    public static string? LastLoadError { get; private set; }

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load() => LoadFrom(SettingsPath);

    public static AppSettings LoadFrom(string path)
    {
        LastLoadError = null;
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<AppSettings>(text);
                if (s is not null && s.Profiles is { Count: > 0 }) return s.Normalized();
                LastLoadError = "settings file was unreadable or did not contain a profile";
            }
            // A previous run may have crashed between write and rename: recover the temp file.
            var tmp = path + ".tmp";
            if (File.Exists(tmp))
            {
                var s2 = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(tmp));
                if (s2 is not null && s2.Profiles is { Count: > 0 })
                {
                    LastLoadError = "recovered settings from an interrupted save";
                    return s2.Normalized();
                }
            }
        }
        catch (Exception e)
        {
            LastLoadError = e.Message;
        }
        return new AppSettings().Normalized();
    }

    public static void Save(AppSettings s) => SaveTo(SettingsPath, s);

    public static void SaveTo(string path, AppSettings s)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(s, WriteOpts);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
        catch { /* settings are best-effort; never crash the UI over them */ }
    }
}

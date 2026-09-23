using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullRGB.SDK;

namespace FullRGB.Config;

/// <summary>Hardware shape of one remembered zone. No user settings live here.</summary>
public sealed class CachedZone
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int ZoneType { get; set; }
    public uint LedsMin { get; set; }
    public uint LedsMax { get; set; }
    public uint LedsCount { get; set; }
}

/// <summary>
/// A device the app has answered for at least once. Remembering the inventory is what lets the app
/// (a) say which parts it knows about before the engine has answered, (b) skip the slow
/// "poll until the list stops growing" wait when the same hardware answers immediately, and
/// (c) keep the user's per-device settings when a part is missing from a single scan.
/// </summary>
public sealed class CachedDevice
{
    /// <summary>RgbController.Key — "name@location", the same identity the profiles are keyed by.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Location { get; set; } = "";
    public uint DeviceType { get; set; }
    public int LedCount { get; set; }
    public List<CachedZone> Zones { get; set; } = new();

    /// <summary>Last scan this device answered for. Drives the retention sweep.</summary>
    public DateTime LastSeenUtc { get; set; }
    public int SeenCount { get; set; }
}

/// <summary>
/// The inventory of RGB parts this machine has ever shown, persisted next to settings.json.
///
/// Deliberately a SEPARATE file from settings.json: settings are the user's data (backed up,
/// exported, imported) while this is a hardware observation that must be safe to delete. A
/// corrupt cache can therefore never cost the user their lighting configuration.
///
/// The merge policy is the important part — it is what makes the cache trustworthy:
///   * a device that answers now is refreshed (zones, LED count, LastSeenUtc, SeenCount);
///   * a device that does NOT answer is KEPT, not dropped. A cold boot that only brings up three
///     of four devices must not be allowed to erase the fourth one's identity (or its settings);
///   * a device that has not answered for <see cref="KeepMissingDays"/> is swept, so genuinely
///     removed hardware does not accumulate forever.
/// </summary>
public sealed class DeviceCache
{
    /// <summary>How long a device may stay silent before it is forgotten. Generous on purpose:
    /// a machine that is off for a holiday must not lose its fan.</summary>
    public const int KeepMissingDays = 30;

    /// <summary>Hard cap so a buggy engine reporting phantom devices cannot grow the file forever.</summary>
    private const int MaxDevices = 200;

    public int Version { get; set; } = 1;
    public DateTime SavedUtc { get; set; }
    public string Signature { get; set; } = "";
    public List<CachedDevice> Devices { get; set; } = new();

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string FilePath => Path.Combine(ProfileStore.Dir, "devices.json");

    // ---------- building ----------

    /// <summary>Snapshot of a live scan, with no previous knowledge merged in.</summary>
    public static DeviceCache From(IEnumerable<RgbController> devices, DateTime nowUtc)
        => new DeviceCache().Merge(devices, nowUtc);

    /// <summary>
    /// Folds a live scan into this cache and returns the RESULT — this instance is never modified,
    /// so a caller holding an older snapshot cannot have it silently emptied by a later sweep.
    /// Pure (no file IO) so <c>--rendertest</c> can assert the policy.
    /// </summary>
    public DeviceCache Merge(IEnumerable<RgbController> devices, DateTime nowUtc)
    {
        var byKey = new Dictionary<string, CachedDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Devices)
            if (!string.IsNullOrEmpty(d.Key)) byKey[d.Key] = d;

        foreach (var dev in devices)
        {
            if (string.IsNullOrEmpty(dev.Key)) continue;
            byKey[dev.Key] = new CachedDevice
            {
                Key = dev.Key,
                Name = dev.Name,
                Vendor = dev.Vendor,
                Location = dev.Location,
                DeviceType = dev.DeviceType,
                LedCount = dev.LedCount,
                Zones = dev.Zones.Select(z => new CachedZone
                {
                    Index = z.Index,
                    Name = z.Name,
                    ZoneType = z.ZoneType,
                    LedsMin = z.LedsMin,
                    LedsMax = z.LedsMax,
                    LedsCount = z.LedsCount,
                }).ToList(),
                LastSeenUtc = nowUtc,
                SeenCount = (byKey.TryGetValue(dev.Key, out var old) ? old.SeenCount : 0) + 1,
            };
        }

        var cutoff = nowUtc - TimeSpan.FromDays(KeepMissingDays);
        var result = new DeviceCache
        {
            Version = Version,
            SavedUtc = nowUtc,
            Devices = byKey.Values
                .Where(d => d.LastSeenUtc >= cutoff)
                .OrderByDescending(d => d.LastSeenUtc)
                .ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDevices)
                .ToList(),
        };
        result.Signature = result.ComputeSignature();
        return result;
    }

    /// <summary>Keys AND names of everything remembered — what <see cref="Profile.PruneTo"/> must not delete.</summary>
    public IEnumerable<string> KnownKeys()
    {
        foreach (var d in Devices)
        {
            if (!string.IsNullOrEmpty(d.Key)) yield return d.Key;
            if (!string.IsNullOrEmpty(d.Name)) yield return d.Name;
        }
    }

    public bool Has(string key) => Devices.Any(d =>
        d.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
        d.Name.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Human-readable list of the remembered devices a fresh scan did NOT report.</summary>
    public List<string> MissingFrom(IEnumerable<RgbController> present)
    {
        var seen = present.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Devices.Where(d => !seen.Contains(d.Key))
                      .Select(d => d.Name)
                      .ToList();
    }

    /// <summary>
    /// Identity of the inventory: which devices, how many zones each, how many LEDs. Two scans with
    /// the same signature describe the same hardware — used to report "nothing changed".
    /// </summary>
    public string ComputeSignature() => Hash(string.Join("\n", Devices
        .Select(d => $"{d.Key}|{d.Zones.Count}|{d.LedCount}")
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)));

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    // ---------- persistence ----------

    public static DeviceCache Load() => LoadFrom(FilePath) ?? new DeviceCache();

    public static DeviceCache? LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var cache = JsonSerializer.Deserialize<DeviceCache>(File.ReadAllText(path));
            if (cache?.Devices is null) return null;
            cache.Devices = cache.Devices.Where(d => !string.IsNullOrEmpty(d.Key)).ToList();
            cache.Signature = cache.ComputeSignature();
            return cache;
        }
        catch { return null; }   // a bad cache is a cache miss, never a startup failure
    }

    public static void Save(DeviceCache cache) => SaveTo(FilePath, cache);

    public static void SaveTo(string path, DeviceCache cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, WriteOpts));
            File.Move(tmp, path, true);
        }
        catch { /* the cache is best-effort: never crash the UI over it */ }
    }
}

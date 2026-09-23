using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FullRGB.Effects;

namespace FullRGB.Config;

/// <summary>
/// Single-profile Import/Export as a shareable file or a short code.
///
/// The envelope is versioned on purpose: a friend sending "my Gaming profile" must not depend on
/// the exact AppSettings shape. The payload is a full <see cref="Profile"/> (effects, overrides,
/// zone sizes, calibration) — device keys inside it are harmless on another machine because
/// <see cref="Profile.PruneTo"/> removes overrides that match no present device.
///
/// Short code = "FRGB1-" + Base64Url(gzip(json)) — small enough to paste into a chat.
/// </summary>
public static class ProfileShare
{
    public const string Prefix = "FRGB1-";

    private sealed class Envelope
    {
        [JsonPropertyName("app")] public string App { get; set; } = "FullRGB";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "profile";
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("exportedAt")] public string ExportedAt { get; set; } = "";
        [JsonPropertyName("profile")] public Profile? Profile { get; set; }
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes one profile to the shareable JSON envelope.</summary>
    public static string ExportJson(Profile profile)
    {
        var env = new Envelope
        {
            ExportedAt = DateTime.UtcNow.ToString("o"),
            Profile = EffectEngine.Clone(profile),
        };
        return JsonSerializer.Serialize(env, Opts);
    }

    /// <summary>Parses an envelope (or a bare settings-style JSON containing exactly one profile
    /// is NOT accepted — profiles come from the envelope only, so an accidental full-settings
    /// import cannot wipe the receiver's names).</summary>
    public static Profile? ImportJson(string json, out string error)
    {
        error = "";
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(json);
            if (env?.Profile is null) { error = "not a FullRGB profile file"; return null; }
            if (!string.Equals(env.App, "FullRGB", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(env.Kind, "profile", StringComparison.OrdinalIgnoreCase))
            { error = "not a FullRGB profile file"; return null; }
            if (env.Version < 1 || env.Version > 2) { error = $"unsupported profile version {env.Version}"; return null; }
            var p = env.Profile;
            // Same repair pass AppSettings.Normalized() runs for stored profiles (no Normalized()
            // on Profile itself — keep the two call sites in sync).
            p.DeviceOverrides ??= new();
            p.ZoneOverrides ??= new();
            p.ExcludedDevices ??= new();
            p.ZoneSizes ??= new();
            p.Calibrations ??= new();
            p.ZoneCalibrations ??= new();
            p.GlobalEffect ??= new EffectDef();
            p.GlobalEffect.Normalized();
            foreach (var v in p.DeviceOverrides.Values) v?.Normalized();
            foreach (var v in p.ZoneOverrides.Values) v?.Normalized();
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Imported";
            return p;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Compact share code: FRGB1-… (gzip + URL-safe base64, no padding).</summary>
    public static string ExportCode(Profile profile)
    {
        string json = ExportJson(profile);
        byte[] raw = Encoding.UTF8.GetBytes(json);
        using var src = new MemoryStream(raw);
        using var gz = new MemoryStream();
        using (var zip = new GZipStream(gz, CompressionLevel.SmallestSize))
            src.CopyTo(zip);
        return Prefix + ToUrlSafe(gz.ToArray());
    }

    public static Profile? ImportCode(string code, out string error)
    {
        error = "";
        try
        {
            string s = (code ?? "").Trim();
            if (s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) s = s[Prefix.Length..];
            // users may paste with line breaks or a trailing quote from a chat client
            s = s.Replace("\r", "").Replace("\n", "").Trim('"', ' ', '\t');
            if (s.Length < 8) { error = "code too short"; return null; }
            byte[] gz = FromUrlSafe(s);
            using var src = new MemoryStream(gz);
            using var outMs = new MemoryStream();
            using (var zip = new GZipStream(src, CompressionMode.Decompress))
                zip.CopyTo(outMs);
            return ImportJson(Encoding.UTF8.GetString(outMs.ToArray()), out error);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Gives the imported profile a unique local name (Gaming → Gaming (2)).</summary>
    public static string DedupeName(IEnumerable<string> existing, string baseName)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (taken.Add(baseName)) return baseName;
        for (int n = 2; n < 100; n++)
            if (taken.Add($"{baseName} ({n})")) return $"{baseName} ({n})";
        return baseName + " " + Guid.NewGuid().ToString("N")[..4];
    }

    /// <summary>Filesystem-safe version of a profile name, for export file suggestions.</summary>
    public static string SafeName2(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "profile" : clean;
    }

    private static string ToUrlSafe(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromUrlSafe(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        int pad = (4 - b64.Length % 4) % 4;
        return Convert.FromBase64String(b64 + new string('=', pad));
    }
}

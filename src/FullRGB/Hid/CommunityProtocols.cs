using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullRGB.Hid;

/// <summary>
/// ONE community-contributed protocol definition for a driverless OEM mouse/keyboard.
/// Authors share a small JSON file; FullRGB imports it into %APPDATA%\FullRGB\hid\ and
/// matches it against detected devices by VID:PID.
///
/// SAFETY MODEL (this is the part that must never soften):
///  • READ is the default and needs nothing: probes are read-only feature-report requests,
///    the same class of traffic the app already uses for detection.
///  • WRITE only happens when ALL of these hold at the same moment:
///      1. the file itself sets "experimentalWrite": true (the author takes responsibility),
///      2. the user turns on the global switch in Hardware → Community protocols
///         (AppSettings.HidExperimentalWrite, persisted),
///      3. the user double-confirms per test paint — two dialogs on EVERY click, the second
///         one requiring the device's VID:PID to be typed out (see MainWindow.TestPaintCommunity),
///      4. the payload length is bounded by the file's declared report length (8..64 bytes).
///  • Only the endpoints the file declares are ever opened, matched by the device's own
///    interface path — no scanning arbitrary devices for writable collections.
/// </summary>
public sealed class HidProtocolFile
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "fullrgb.hid/1";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("vid")] public string Vid { get; set; } = "";
    [JsonPropertyName("pid")] public string Pid { get; set; } = "";
    [JsonPropertyName("experimentalWrite")] public bool ExperimentalWrite { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("probe")] public HidProbe? Probe { get; set; }
    [JsonPropertyName("paint")] public HidPaint? Paint { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    [JsonIgnore] public ushort VidNum { get; internal set; }
    [JsonIgnore] public ushort PidNum { get; internal set; }
    /// <summary>SHA-256 of the imported file bytes — shown in the UI so two versions of the
    /// same protocol are tellable apart.</summary>
    [JsonIgnore] public string Sha256 { get; internal set; } = "";
    [JsonIgnore] public string VidPid => $"{VidNum:X4}:{PidNum:X4}";

    public bool CanWrite => ExperimentalWrite && App.Settings.HidExperimentalWrite;

    /// <summary>Strict parse + validation. Returns null and fills error for anything unsafe.</summary>
    public static HidProtocolFile? Parse(string json, string sourceName, out string error)
    {
        error = "";
        HidProtocolFile? f;
        try
        {
            f = JsonSerializer.Deserialize<HidProtocolFile>(json);
        }
        catch (Exception e)
        {
            error = $"invalid JSON: {e.Message}";
            return null;
        }
        if (f is null) { error = "empty file"; return null; }
        if (!f.Schema.Equals("fullrgb.hid/1", StringComparison.Ordinal))
        { error = $"unsupported schema '{f.Schema}' (expected fullrgb.hid/1)"; return null; }
        if (string.IsNullOrWhiteSpace(f.Name)) { error = "missing name"; return null; }

        if (!TryHex4(f.Vid, out var vidN) || !TryHex4(f.Pid, out var pidN))
        { error = "vid/pid must be 4 hex digits (e.g. \"2A7A\")"; return null; }
        f.VidNum = vidN;
        f.PidNum = pidN;

        if (f.Probe is null && f.Paint is null)
        { error = "nothing to do: define at least a probe or a paint section"; return null; }

        if (f.Probe is not null && !f.Probe.Validate(out error)) return null;
        if (f.Paint is not null && !f.Paint.Validate(out error)) return null;

        f.Source = sourceName;
        return f;
    }

    private static bool TryHex4(string? s, out ushort v)
    {
        v = 0;
        return !string.IsNullOrWhiteSpace(s)
            && s.Trim().Length == 4
            && ushort.TryParse(s.Trim(), System.Globalization.NumberStyles.HexNumber, null, out v);
    }
}

/// <summary>A read-only probe: which feature report to read and how to log it.</summary>
public sealed class HidProbe
{
    [JsonPropertyName("usagePage")] public int UsagePage { get; set; }
    [JsonPropertyName("usage")] public int Usage { get; set; }
    [JsonPropertyName("reportId")] public int ReportId { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; } = 8;

    public bool Validate(out string error)
    {
        error = "";
        if (ReportId < 0 || ReportId > 255) { error = "probe.reportId must be 0..255"; return false; }
        if (Length < 2 || Length > 64) { error = "probe.length must be 2..64"; return false; }
        return true;
    }
}

/// <summary>
/// A guarded paint payload: one report that fills the device's LEDs with a solid colour.
/// Deliberately minimal — solid colours only, no animation, no arbitrary byte injection.
/// The frame is prefix + RGB triple(s) (order per "order") + zero tail, total == length.
/// </summary>
public sealed class HidPaint
{
    [JsonPropertyName("usagePage")] public int UsagePage { get; set; }
    [JsonPropertyName("usage")] public int Usage { get; set; }
    [JsonPropertyName("reportId")] public int ReportId { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; }
    [JsonPropertyName("prefix")] public string Prefix { get; set; } = "";
    [JsonPropertyName("tail")] public string Tail { get; set; } = "";
    [JsonPropertyName("order")] public string Order { get; set; } = "RGB";   // RGB | BGR
    [JsonPropertyName("rgbSlots")] public int RgbSlots { get; set; } = 1;    // repeat the colour N times

    public bool Validate(out string error)
    {
        error = "";
        if (ReportId < 0 || ReportId > 255) { error = "paint.reportId must be 0..255"; return false; }
        if (Length < 8 || Length > 64) { error = "paint.length must be 8..64"; return false; }
        if (RgbSlots < 1 || RgbSlots > 20) { error = "paint.rgbSlots must be 1..20"; return false; }
        if (Order is not ("RGB" or "BGR")) { error = "paint.order must be RGB or BGR"; return false; }
        if (!TryHexBytes(Prefix, out int plen)) { error = "paint.prefix must be hex bytes (e.g. \"06 01\")"; return false; }
        if (!TryHexBytes(Tail, out int tlen)) { error = "paint.tail must be hex bytes"; return false; }
        if (1 + plen + 3 * RgbSlots + tlen > Length)
        { error = "paint does not fit: 1 + prefix + 3*rgbSlots + tail must be <= length"; return false; }
        return true;
    }

    internal static bool TryHexBytes(string? hex, out int byteCount)
    {
        byteCount = 0;
        if (string.IsNullOrWhiteSpace(hex)) return true;   // empty = no bytes
        var parts = hex.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
            if (p.Length is (1 or 2) && byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out _))
                byteCount++;
            else return false;
        return true;
    }
}

/// <summary>
/// Loads and imports community protocol files. Everything is validated at LOAD time too, so a
/// hand-edited store file cannot smuggle in what Import would have rejected.
/// </summary>
public static class CommunityStore
{
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB", "hid");

    /// <summary>Every valid protocol currently in the store (invalid files are reported, not fatal).</summary>
    public static (List<HidProtocolFile> Valid, List<string> Errors) Load()
    {
        var valid = new List<HidProtocolFile>();
        var errors = new List<string>();
        try
        {
            if (!Directory.Exists(Dir)) return (valid, errors);
            foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json").OrderBy(f => f.Name))
            {
                try
                {
                    var text = File.ReadAllText(f.FullName);
                    var proto = HidProtocolFile.Parse(text, f.Name, out var error);
                    if (proto is null) { errors.Add($"{f.Name}: {error}"); continue; }
                    proto.Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.FullName))).ToLowerInvariant()[..12];
                    valid.Add(proto);
                }
                catch (Exception e) { errors.Add($"{f.Name}: {e.Message}"); }
            }
        }
        catch { }
        return (valid, errors);
    }

    public static HidProtocolFile? Find(IEnumerable<HidProtocolFile> protocols, ushort vid, ushort pid)
        => protocols.FirstOrDefault(p => p.VidNum == vid && p.PidNum == pid);

    /// <summary>Validates then copies a file into the store (never executes anything).
    /// Returns null + error for an invalid or unreadable file.</summary>
    public static HidProtocolFile? Import(string path, out string error)
    {
        error = "";
        try
        {
            string text = File.ReadAllText(path);
            string name = Path.GetFileName(path);
            var proto = HidProtocolFile.Parse(text, name, out error);
            if (proto is null) return null;
            Directory.CreateDirectory(Dir);
            string target = Path.Combine(Dir, SafeFileName(proto.Name) + ".json");
            int n = 2;
            while (File.Exists(target)) target = Path.Combine(Dir, SafeFileName(proto.Name) + $"-{n++}.json");
            File.WriteAllText(target, text);
            proto.Source = Path.GetFileName(target);
            proto.Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant()[..12];
            return proto;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    public static string Remove(string source)
    {
        try
        {
            // Defense in depth: callers today pass a name that came from a directory listing,
            // but Path.Combine happily resolves "..\..\something", so never build a path from
            // an unvalidated string.
            if (!IsSafeStoreName(source)) return "invalid protocol file name";
            var path = Path.Combine(Dir, source);
            if (File.Exists(path)) { File.Delete(path); return ""; }
            return "file not found";
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>True when <paramref name="name"/> is a plain file name inside the store:
    /// no separators, no "..", no rooted path, nothing that escapes <see cref="Dir"/>.</summary>
    internal static bool IsSafeStoreName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) return false;
        if (Path.IsPathRooted(name)) return false;
        // The final authority: the name must round-trip through GetFileName unchanged.
        return string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal);
    }

    private static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "protocol" : clean;
    }
}

using System.Text.Json;

namespace FullRGB.Automation;

/// <summary>
/// Parses and executes one text command against an <see cref="IControlTarget"/>.
/// Shared by every transport so the CLI, the named pipe and the HTTP API behave EXACTLY
/// the same (and are tested once). Pure wiring: the target is called on the calling
/// thread — transports marshal.
///
/// Text protocol (named pipe / CLI): one command per line, e.g.
///   set-profile gaming        → OK
///   game-event hp 0.42        → OK
///   status                    → JSON
/// Unknown command → "ERR unknown command (try HELP)".
/// </summary>
public static class CommandDispatcher
{
    public static readonly string HelpText =
        "FullRGB control commands:\n" +
        "  set-profile <name>      switch profile\n" +
        "  profiles                list profile names\n" +
        "  set-effect <name>       set the global effect (solid, gradient, rainbow, cycle, breathing,\n" +
        "                          wave, comet, blink, fire, temp, audio, custom, spectrum, scanner,\n" +
        "                          sparkle, plasma, ambient, gaming, gamepulse)\n" +
        "  set-color <#RRGGBB>     primary colour of the global effect\n" +
        "  set-color2 <#RRGGBB>    secondary colour\n" +
        "  set-brightness <0..1>   global effect brightness (accepts 0..100 too)\n" +
        "  set-speed <0..1>        global effect speed\n" +
        "  power <on|off>          start/stop the effect loops\n" +
        "  blackout                paint every LED black once\n" +
        "  game-event <name> [v]   feed the GamePulse effect (hp, hit, damage, heal, death,\n" +
        "                          respawn, levelup, goal, explode)\n" +
        "  rescan                  re-detect devices\n" +
        "  status                  one-line JSON session summary\n" +
        "  version                 app version";

    /// <summary>Executes one command line. Returns the reply text; "OK" on success.</summary>
    public static string Execute(IControlTarget target, string? line)
    {
        var (cmd, arg) = Split(line);
        if (cmd.Length == 0) return "ERR empty command";
        try
        {
            switch (cmd)
            {
                case "help":
                case "?":
                    return HelpText;

                case "version":
                    return "FullRGB " + (System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString(3) ?? "?");

                case "status":
                    return target.StatusJson();

                case "profiles":
                    var names = target.ProfileNames();
                    return names.Count == 0 ? "" : string.Join("\n", names);

                case "set-profile":
                case "profile":
                    return arg.Length == 0 ? "ERR missing profile name"
                        : target.SetProfile(arg) ? "OK" : "ERR unknown profile: " + arg;

                case "set-effect":
                case "effect":
                    return arg.Length == 0 ? "ERR missing effect name"
                        : target.SetEffect(arg) ? "OK" : "ERR unknown effect: " + arg;

                case "set-color":
                case "color":
                    return ParseHex(arg, out var hex1) ? (target.SetColor(hex1) ? "OK" : "ERR no engine")
                        : "ERR bad colour (use #RRGGBB): " + arg;

                case "set-color2":
                case "color2":
                    return ParseHex(arg, out var hex2) ? (target.SetColor(hex2, secondary: true) ? "OK" : "ERR no engine")
                        : "ERR bad colour (use #RRGGBB): " + arg;

                case "set-brightness":
                case "brightness":
                    if (!TryUnit(arg, out var b)) return "ERR value must be 0..1 (or 0..100): " + arg;
                    return target.SetBrightness(b) ? "OK" : "ERR no engine";

                case "set-speed":
                case "speed":
                    if (!TryUnit(arg, out var s)) return "ERR value must be 0..1 (or 0..100): " + arg;
                    return target.SetSpeed(s) ? "OK" : "ERR no engine";

                case "power":
                {
                    string p = arg.ToLowerInvariant();
                    if (p is "on" or "1" or "true") return target.Power(true) ? "OK" : "ERR no engine";
                    if (p is "off" or "0" or "false") return target.Power(false) ? "OK" : "ERR no engine";
                    return "ERR use: power on|off";
                }

                case "stop":
                    return target.Power(false) ? "OK" : "ERR no engine";
                case "start":
                    return target.Power(true) ? "OK" : "ERR no engine";

                case "blackout":
                case "alloff":
                    return target.Blackout() ? "OK" : "ERR no engine";

                case "game-event":
                case "event":
                {
                    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length == 0) return "ERR use: game-event <name> [value]";
                    double v = double.NaN;
                    if (parts.Length > 1 && !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out v))
                        return "ERR bad value: " + parts[1];
                    target.PushGameEvent(parts[0], v);
                    return "OK";
                }

                case "rescan":
                case "scan":
                    return target.Rescan() ? "OK" : "ERR engine offline";

                default:
                    return "ERR unknown command '" + cmd + "' (try HELP)";
            }
        }
        catch (Exception e)
        {
            return "ERR " + e.Message;
        }
    }

    /// <summary>Splits "cmd arg..." — the arg keeps inner spaces (profile names may contain them).</summary>
    public static (string cmd, string arg) Split(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0) return ("", "");
        int sp = s.IndexOf(' ');
        if (sp < 0) return (s.ToLowerInvariant(), "");
        return (s[..sp].ToLowerInvariant(), s[(sp + 1)..].Trim());
    }

    private static bool ParseHex(string input, out string hex)
    {
        hex = "";
        var s = (input ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) return false;
        hex = "#" + s.ToUpperInvariant();
        return true;
    }

    /// <summary>Accepts 0..1 and 0..100 ("42" means 42 % only when > 1 — 1.0 stays 100 %).</summary>
    internal static bool TryUnit(string input, out double v)
    {
        v = 0;
        if (!double.TryParse((input ?? "").TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture, out var raw))
            return false;
        v = raw > 1.0 ? Math.Clamp(raw / 100.0, 0, 1) : Math.Clamp(raw, 0, 1);
        return !double.IsNaN(raw);
    }

    // ---------- JSON body helpers for the HTTP routes ----------

    public static string JsonGet(string json, params string[] keys)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            foreach (var k in keys)
                if (doc.RootElement.TryGetProperty(k, out var el))
                {
                    return el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString() ?? "",
                        JsonValueKind.Number => el.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => el.GetRawText(),
                    };
                }
        }
        catch { }
        return "";
    }

    public static string JsonOk(string extraJson = "")
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return "{\"ok\":true}";
        string inner = extraJson.Trim().TrimStart('{').TrimEnd('}');
        return inner.Length == 0 ? "{\"ok\":true}" : "{\"ok\":true," + inner + "}";
    }

    public static string JsonErr(string message)
        => "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(message) + "}";
}

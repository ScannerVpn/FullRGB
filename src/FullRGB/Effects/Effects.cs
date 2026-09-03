namespace FullRGB.Effects;

/// <summary>
/// Effect kinds. Values are serialized as NUMBERS in settings.json, so new entries must be
/// APPENDED — never inserted in the middle, or existing user profiles change effect.
/// </summary>
public enum EffectType
{
    Solid = 0, Rainbow = 1, Breathing = 2, Wave = 3, Blink = 4,
    Temperature = 5, AudioVU = 6, Custom = 7,
    // round 7 additions
    Gradient = 8,     // static primary -> secondary across the strip
    ColorCycle = 9,   // whole strip cycles hue in unison
    Comet = 10,       // travelling dot with a fading tail
    Fire = 11,        // flickering ember palette
}

/// <summary>Serializable effect definition (stored in profiles).</summary>
public sealed class EffectDef
{
    public EffectType Type { get; set; } = EffectType.Rainbow;
    public string ColorHex { get; set; } = "#00E5FF";       // primary
    public string Color2Hex { get; set; } = "#7C4DFF";      // secondary (gradient/blink)
    public double Speed { get; set; } = 0.5;                // 0..1
    public double Brightness { get; set; } = 0.8;           // 0..1
    public string TempSensor { get; set; } = "cpu";         // cpu|gpu
    public double TempLow { get; set; } = 35;
    public double TempHigh { get; set; } = 85;
    public string CustomPixels { get; set; } = "FF0000,00FF00,0000FF"; // comma hex list, cycled
    public string Direction { get; set; } = "forward";      // forward|reverse

    /// <summary>
    /// True = every zone renders with the same phase, so all devices show identical colour
    /// at the same moment (what users expect from "one colour everywhere").
    /// False = each zone is offset, which looks like a wave travelling across the case.
    /// </summary>
    public bool SyncZones { get; set; } = true;

    /// <summary>Which audio band drives the music effect: level|bass|mid|treble.</summary>
    public string AudioBand { get; set; } = "level";

    public static EffectDef Default() => new();

    /// <summary>True when this effect animates over time (used to skip needless work/UI).</summary>
    public bool IsAnimated => Type is not (EffectType.Solid or EffectType.Gradient);
}

/// <summary>Per-frame context passed to renderers.</summary>
public sealed class EffectContext
{
    public double Time;            // seconds
    public double? CpuTemp;        // null = unavailable
    public double? GpuTemp;
    public double AudioLevel;      // 0..1 overall
    public double AudioBass, AudioMid, AudioTreble; // 0..1 bands
}

public static class EffectRenderer
{
    /// <summary>
    /// Renders one frame.
    /// <paramref name="seed"/> is the per-zone phase seed: 0 for every zone when
    /// EffectDef.SyncZones is on (identical output), otherwise a per-zone number that
    /// offsets the effect in TIME and SPACE. Before round 7 the seed was only honoured by
    /// Rainbow, so "unsynced" silently did nothing for wave/blink/breathing/custom.
    /// </summary>
    public static byte[] Render(EffectDef e, int ledCount, int seed, EffectContext ctx)
    {
        var rgb = new byte[Math.Max(0, ledCount) * 3];
        if (rgb.Length == 0) return rgb;

        double speed = SpeedFactor(e.Speed);
        // one shared phase model for every effect: seed shifts time and LED position
        double t = ctx.Time + seed * 0.37;
        int spatial = seed * 5;
        int dir = e.Direction == "reverse" ? -1 : 1;

        switch (e.Type)
        {
            case EffectType.Solid:
            {
                var c = ParseHex(e.ColorHex, e.Brightness);
                Fill(rgb, c.r, c.g, c.b);
                break;
            }
            case EffectType.Gradient:
            {
                var c1 = ParseHex(e.ColorHex, e.Brightness);
                var c2 = ParseHex(e.Color2Hex, e.Brightness);
                for (int i = 0; i < ledCount; i++)
                {
                    double k = ledCount <= 1 ? 0 : (double)i / (ledCount - 1);
                    if (dir < 0) k = 1 - k;
                    Set(rgb, i, Lerp(c1.r, c2.r, k), Lerp(c1.g, c2.g, k), Lerp(c1.b, c2.b, k));
                }
                break;
            }
            case EffectType.Rainbow:
            {
                double phase = Frac(t * speed * 0.25 + seed * 0.15);
                for (int i = 0; i < ledCount; i++)
                {
                    double pos = (double)((i + spatial) * dir) / ledCount;
                    var (r, g, b) = Hsv(Frac(phase + pos), 1.0, e.Brightness);
                    Set(rgb, i, r, g, b);
                }
                break;
            }
            case EffectType.ColorCycle:
            {
                // whole strip one colour, hue rotating — the "breathing colour" look
                var (r, g, b) = Hsv(Frac(t * speed * 0.12), 1.0, e.Brightness);
                Fill(rgb, r, g, b);
                break;
            }
            case EffectType.Breathing:
            {
                var c = ParseHex(e.ColorHex, e.Brightness);
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t * speed * 0.4);
                Fill(rgb, Scale(c.r, w), Scale(c.g, w), Scale(c.b, w));
                break;
            }
            case EffectType.Wave:
            {
                var c1 = ParseHex(e.ColorHex, e.Brightness);
                var c2 = ParseHex(e.Color2Hex, e.Brightness);
                // Wavelength is capped at 30 LEDs instead of ledCount/2, so a 120-LED header
                // and a 34-LED fan show the SAME physical wave size rather than one stretched
                // wave per zone.
                double width = Math.Max(6, Math.Min(ledCount, 30));
                double shift = t * speed * 12.0 * dir;
                for (int i = 0; i < ledCount; i++)
                {
                    double k = 0.5 + 0.5 * Math.Sin(2 * Math.PI * ((i + spatial + shift) / width));
                    Set(rgb, i, Lerp(c1.r, c2.r, k), Lerp(c1.g, c2.g, k), Lerp(c1.b, c2.b, k));
                }
                break;
            }
            case EffectType.Comet:
            {
                var c = ParseHex(e.ColorHex, e.Brightness);
                double tail = Math.Max(3, ledCount * 0.25);
                double head = Frac(t * speed * 0.25) * ledCount;
                for (int i = 0; i < ledCount; i++)
                {
                    double p = dir > 0 ? i : ledCount - 1 - i;
                    double d = head - p;
                    if (d < 0) d += ledCount;                 // wrap around the strip
                    double k = d <= tail ? 1.0 - d / tail : 0; // linear fade behind the head
                    k *= k;                                    // gamma-ish falloff, looks sharper
                    Set(rgb, i, Scale(c.r, k), Scale(c.g, k), Scale(c.b, k));
                }
                break;
            }
            case EffectType.Fire:
            {
                // deterministic pseudo-flicker: same frame index => same frame (no strobing jitter)
                long frame = (long)(t * (4 + speed * 20));
                for (int i = 0; i < ledCount; i++)
                {
                    double n1 = Noise(i + spatial, frame);
                    double n2 = Noise(i + spatial, frame + 1);
                    double f = t * (4 + speed * 20) - frame;
                    double heat = 0.35 + 0.65 * (n1 * (1 - f) + n2 * f);
                    byte r = Scale(255, heat * e.Brightness);
                    byte g = Scale(90, Math.Pow(heat, 2.2) * e.Brightness);
                    byte b = Scale(12, Math.Pow(heat, 5) * e.Brightness);
                    Set(rgb, i, r, g, b);
                }
                break;
            }
            case EffectType.Blink:
            {
                var c = ParseHex(e.ColorHex, e.Brightness);
                bool on = Frac(t * speed * 0.8) < 0.5;
                if (on) Fill(rgb, c.r, c.g, c.b); else Fill(rgb, 0, 0, 0);
                break;
            }
            case EffectType.Temperature:
            {
                double? temp = e.TempSensor == "gpu" ? ctx.GpuTemp : ctx.CpuTemp;
                double norm = temp is null
                    ? (0.5 + 0.5 * Math.Sin(2 * Math.PI * t * 0.1)) // demo sweep when sensors unavailable
                    : Math.Clamp((temp.Value - e.TempLow) / Math.Max(1, e.TempHigh - e.TempLow), 0, 1);
                var cold = ParseHex("#00B0FF", 1.0);
                var hot = ParseHex("#FF2200", 1.0);
                byte r = Lerp(cold.r, hot.r, norm), g = Lerp(cold.g, hot.g, norm), b = Lerp(cold.b, hot.b, norm);
                double scale = (0.25 + 0.75 * norm) * e.Brightness;
                Fill(rgb, Scale(r, scale), Scale(g, scale), Scale(b, scale));
                break;
            }
            case EffectType.AudioVU:
            {
                // Level meter. When there is NO sound the strip must go dark — showing the
                // secondary colour at low brightness made a silent PC look permanently lit.
                var peak = ParseHex(e.ColorHex, e.Brightness);
                var tailC = ParseHex(e.Color2Hex, e.Brightness);
                double level = Math.Clamp(BandValue(e.AudioBand, ctx), 0, 1);
                const double gate = 0.02;           // noise floor: below this, everything off
                if (level <= gate)
                {
                    Fill(rgb, 0, 0, 0);
                    break;
                }
                int lit = (int)Math.Round(level * ledCount);
                for (int i = 0; i < ledCount; i++)
                {
                    int pos = dir < 0 ? ledCount - 1 - i : i;
                    if (pos < lit)
                    {
                        double k = lit <= 1 ? 0 : (double)pos / Math.Max(1, lit - 1);
                        Set(rgb, i, Lerp(peak.r, tailC.r, k), Lerp(peak.g, tailC.g, k), Lerp(peak.b, tailC.b, k));
                    }
                    else Set(rgb, i, 0, 0, 0);
                }
                break;
            }
            case EffectType.Custom:
            {
                var pixels = e.CustomPixels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (pixels.Length == 0) { Fill(rgb, 0, 0, 0); break; }
                var pal = pixels.Select(p => ParseHex(p, e.Brightness)).ToArray();
                int L = pal.Length;
                // Custom indexes the palette with an integer floor, so a time-based per-zone
                // offset can land on a whole multiple of the palette length and make
                // "unsynced" look synced. Use the BASE time plus a palette offset that is
                // guaranteed to be a non-zero residue mod L instead.
                double shift = ctx.Time * speed * 4.0 * dir;
                int zoneShift = seed == 0 || L < 2 ? 0 : 1 + Math.Abs(seed) % (L - 1);
                for (int i = 0; i < ledCount; i++)
                {
                    int idx = (int)Math.Floor(i + zoneShift + shift);
                    idx = ((idx % L) + L) % L;
                    Set(rgb, i, pal[idx].r, pal[idx].g, pal[idx].b);
                }
                break;
            }
        }
        return rgb;
    }

    /// <summary>Which audio figure the music effect follows.</summary>
    public static double BandValue(string band, EffectContext ctx) => band switch
    {
        "bass" => ctx.AudioBass,
        "mid" => ctx.AudioMid,
        "treble" => ctx.AudioTreble,
        _ => ctx.AudioLevel,
    };

    private static double SpeedFactor(double s01) => 0.15 + Math.Clamp(s01, 0, 1) * 3.85; // 0..1 -> 0.15..4

    private static double Frac(double x) => x - Math.Floor(x);

    private static byte Lerp(byte a, byte b, double k) => (byte)Math.Clamp(Math.Round(a + (b - a) * k), 0, 255);

    private static byte Scale(byte v, double k) => (byte)Math.Clamp(Math.Round(v * k), 0, 255);

    private static void Set(byte[] rgb, int i, byte r, byte g, byte b)
    {
        rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
    }

    /// <summary>Deterministic 0..1 hash noise — no allocation, no shared Random, thread-safe.</summary>
    private static double Noise(int x, long frame)
    {
        ulong h = (ulong)(x * 374761393L) ^ (ulong)(frame * 668265263L);
        h ^= h >> 13; h *= 1274126177UL; h ^= h >> 16;
        return (h & 0xFFFFFF) / (double)0xFFFFFF;
    }

    /// <summary>Parses "#RRGGBB" (or "RRGGBB") and pre-multiplies brightness. Falls back to cyan.</summary>
    public static (byte r, byte g, byte b) ParseHex(string hex, double brightness)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length == 3) // #ABC shorthand
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length == 6
            && int.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int r)
            && int.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g)
            && int.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
            return (Scale((byte)r, brightness), Scale((byte)g, brightness), Scale((byte)b, brightness));
        return (0, Scale(229, brightness), Scale(255, brightness));
    }

    public static (byte r, byte g, byte b) Hsv(double h, double s, double v)
    {
        h = Frac(h);
        int i = (int)(h * 6) % 6;
        double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return i switch
        {
            0 => (B(v), B(t), B(p)),
            1 => (B(q), B(v), B(p)),
            2 => (B(p), B(v), B(t)),
            3 => (B(p), B(q), B(v)),
            4 => (B(t), B(p), B(v)),
            _ => (B(v), B(p), B(q)),
        };
        static byte B(double x) => (byte)Math.Clamp(x * 255, 0, 255);
    }

    private static void Fill(byte[] rgb, byte r, byte g, byte b)
    {
        for (int i = 0; i < rgb.Length / 3; i++)
        {
            rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
        }
    }
}

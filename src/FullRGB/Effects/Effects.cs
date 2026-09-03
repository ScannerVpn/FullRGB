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
    // music/shape round: APPEND-only (serialized as numbers)
    Spectrum = 12,    // bass|mid|treble mini-bars side by side, each its own colour
    Scanner = 13,     // ping-pong head with tail (KITT-style bounce, unlike Comet's wrap)
    Sparkle = 14,     // dim base with deterministic twinkles
    Plasma = 15,      // flowing 3-stop gradient blobs
}

/// <summary>Serializable effect definition (stored in profiles).</summary>
public sealed class EffectDef
{
    public EffectType Type { get; set; } = EffectType.Rainbow;
    public string ColorHex { get; set; } = "#00E5FF";       // primary
    public string Color2Hex { get; set; } = "#7C4DFF";      // secondary (gradient/blink)
    public string Color3Hex { get; set; } = "#FF9100";      // tertiary (spectrum treble, plasma stop)
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

    /// <summary>Music sensitivity multiplier (0.2..2.5): quiet songs need a boost to dance.</summary>
    public double AudioGain { get; set; } = 1.0;

    /// <summary>White kick-flash strength on music effects (0 = off).</summary>
    public double BeatStrength { get; set; } = 0.0;

    /// <summary>Music shape: bar (edge fill) | mirror (center-out) | pulse (whole strip breathes) | dots (dotted bar).</summary>
    public string AudioMode { get; set; } = "bar";

    /// <summary>Music colouring: gradient (primary→secondary) | palette (CustomPixels) | level (green→red ramp) | rainbow (flowing spectrum).</summary>
    public string AudioColor { get; set; } = "gradient";

    /// <summary>Background for unlit music LEDs. Empty = black (off).</summary>
    public string AudioBgHex { get; set; } = "";

    /// <summary>Peak-hold dot that lingers at the recent maximum and falls slowly.</summary>
    public bool PeakHold { get; set; } = true;

    /// <summary>Wave/Breathing/Blink/Comet/Gradient sample CustomPixels instead of the two colours.</summary>
    public bool UsePalette { get; set; } = false;

    /// <summary>Extra colour stops appended after the fixed boxes (gradient-style effects sample
    /// them all). Capped at 8; empty by default so old profiles render exactly as before.</summary>
    public List<string> ExtraColors { get; set; } = new();

    public static EffectDef Default() => new();

    /// <summary>Repairs nulls from older/hand-edited settings files (missing JSON keys → null).</summary>
    public EffectDef Normalized()
    {
        ColorHex ??= "#00E5FF";
        Color2Hex ??= "#7C4DFF";
        Color3Hex ??= "#FF9100";
        TempSensor = TempSensor == "gpu" ? "gpu" : "cpu";
        CustomPixels ??= "FF0000,00FF00,0000FF";
        Direction = Direction == "reverse" ? "reverse" : "forward";
        AudioBand = AudioBand is "bass" or "mid" or "treble" or "level" ? AudioBand : "level";
        if (double.IsNaN(AudioGain) || AudioGain < 0.2) AudioGain = 0.2;
        if (AudioGain > 2.5) AudioGain = 2.5;
        if (double.IsNaN(BeatStrength) || BeatStrength < 0) BeatStrength = 0;
        if (BeatStrength > 1) BeatStrength = 1;
        AudioMode = AudioMode is "mirror" or "pulse" or "dots" or "bar" ? AudioMode : "bar";
        AudioColor = AudioColor is "palette" or "level" or "rainbow" or "gradient" ? AudioColor : "gradient";
        AudioBgHex ??= "";
        ExtraColors ??= new List<string>();
        // keep only real hex colours, capped, with a '#' prefix so editors render them as-is
        ExtraColors = ExtraColors
            .Select(x => (x ?? "").Trim().TrimStart('#'))
            .Where(x => x.Length == 6 || x.Length == 3)
            .Where(x => x.All(c => Uri.IsHexDigit(c)))
            .Take(8).Select(x => "#" + x.ToUpperInvariant()).ToList();
        return this;
    }

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
    public double Beat;            // 0..1 kick onset envelope
}

/// <summary>Stateful music meter state (peak-hold). One per render loop; the renderer is pure otherwise.</summary>
public sealed class AudioState
{
    public double Peak;
    public double LastTime = double.NaN;
}

/// <summary>Curated colour sets: one click fills the 3 colours + the palette.</summary>
public sealed record Preset(string Name, string[] Colors);

public static class EffectPresets
{
    public static readonly Preset[] All =
    {
        new("Sunset", new[] { "FF6B35", "FF2E63", "FFD166", "FFF3E0" }),
        new("Ocean", new[] { "00E5FF", "2979FF", "7C4DFF", "64FFDA" }),
        new("Forest", new[] { "00FF87", "22C55E", "A3E635", "FDE047" }),
        new("Neon", new[] { "39FF14", "00E5FF", "FF10F0", "FFFF00" }),
        new("Candy", new[] { "FF71CE", "B967FF", "01CDFE", "FFFFFF" }),
        new("Royal", new[] { "7C4DFF", "3D5AFE", "FF4D8D", "FFD166" }),
        new("Ember", new[] { "FF9100", "FF3D00", "FFB300", "FFF8E1" }),
        new("Ice", new[] { "40C4FF", "7C4DFF", "B3E5FC", "FFFFFF" }),
        new("Cherry", new[] { "FF1744", "EC407A", "FFD166", "FFFFFF" }),
        new("Volt", new[] { "C6FF00", "76FF03", "00E676", "18FFFF" }),
        new("Grape", new[] { "D500F9", "7C4DFF", "40C4FF", "FF80AB" }),
        new("Mono Blue", new[] { "448AFF", "2962FF", "82B1FF", "0D47A1" }),
    };

    public static void Apply(EffectDef e, Preset p)
    {
        var c = p.Colors;
        e.ColorHex = "#" + c[0];
        e.Color2Hex = "#" + c[1];
        e.Color3Hex = "#" + c[2];
        e.CustomPixels = string.Join(",", c);
        e.ExtraColors.Clear(); // a preset is a complete look: stale extra stops would corrupt it
    }
}

public static class EffectRenderer
{
    /// <summary>
    /// Renders one frame.
    /// <paramref name="seed"/> is the per-zone phase seed: 0 for every zone when
    /// EffectDef.SyncZones is on (identical output), otherwise a per-zone number that
    /// offsets the effect in TIME and SPACE. Before round 7 the seed was only honoured by
    /// Rainbow, so "unsynced" silently did nothing for wave/blink/breathing/custom.
    /// Pass an <paramref name="audioState"/> to get a peak-hold dot on music effects;
    /// without one the dot sits at the live edge (used by tests and the stateless path).
    /// </summary>
    public static byte[] Render(EffectDef e, int ledCount, int seed, EffectContext ctx)
        => Render(e, ledCount, seed, ctx, null);

    public static byte[] Render(EffectDef e, int ledCount, int seed, EffectContext ctx, AudioState? audioState)
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
                var stops = StopsOrBoxes(e, c1, c2);
                for (int i = 0; i < ledCount; i++)
                {
                    double k = ledCount <= 1 ? 0 : (double)i / (ledCount - 1);
                    if (dir < 0) k = 1 - k;
                    var (r, g, b) = Sample(stops, k);
                    Set(rgb, i, r, g, b);
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
                if (e.UsePalette && TryPalette(e, out var stops) && stops.Length > 0)
                {
                    // travel through the palette while breathing in brightness
                    var (r, g, b) = Sample(stops, Frac(t * speed * 0.12));
                    Fill(rgb, Scale(r, 0.08 + 0.92 * w), Scale(g, 0.08 + 0.92 * w), Scale(b, 0.08 + 0.92 * w));
                }
                else
                {
                    var travel = BoxStops(e, c);
                    if (travel.Length > 1)
                    {
                        // extra boxes: the breathed colour travels through them
                        var (r, g, b) = Sample(travel, Frac(t * speed * 0.12));
                        Fill(rgb, Scale(r, 0.08 + 0.92 * w), Scale(g, 0.08 + 0.92 * w), Scale(b, 0.08 + 0.92 * w));
                    }
                    else Fill(rgb, Scale(c.r, w), Scale(c.g, w), Scale(c.b, w));
                }
                break;
            }
            case EffectType.Wave:
            {
                var c1 = ParseHex(e.ColorHex, e.Brightness);
                var c2 = ParseHex(e.Color2Hex, e.Brightness);
                var stops = StopsOrBoxes(e, c1, c2);
                // Wavelength is capped at 30 LEDs instead of ledCount/2, so a 120-LED header
                // and a 34-LED fan show the SAME physical wave size rather than one stretched
                // wave per zone.
                double width = Math.Max(6, Math.Min(ledCount, 30));
                double shift = t * speed * 12.0 * dir;
                for (int i = 0; i < ledCount; i++)
                {
                    double k = 0.5 + 0.5 * Math.Sin(2 * Math.PI * ((i + spatial + shift) / width));
                    var (r, g, b) = Sample(stops, k);
                    Set(rgb, i, r, g, b);
                }
                break;
            }
            case EffectType.Comet:
            {
                var c = ParseHex(e.ColorHex, e.Brightness);
                TryPalette(e, out var palStops);
                var boxes = BoxStops(e, c);
                // palette wins when enabled, otherwise the boxes (+ extras) colour the head
                var headStops = e.UsePalette && palStops.Length > 0 ? palStops : boxes;
                double tail = Math.Max(3, ledCount * 0.25);
                double head = Frac(t * speed * 0.25) * ledCount;
                var headC = headStops.Length > 1
                    ? Sample(headStops, Frac(head / Math.Max(1, ledCount)))
                    : c;
                for (int i = 0; i < ledCount; i++)
                {
                    double p = dir > 0 ? i : ledCount - 1 - i;
                    double d = head - p;
                    if (d < 0) d += ledCount;                 // wrap around the strip
                    double k = d <= tail ? 1.0 - d / tail : 0; // linear fade behind the head
                    k *= k;                                    // gamma-ish falloff, looks sharper
                    Set(rgb, i, Scale(headC.r, k), Scale(headC.g, k), Scale(headC.b, k));
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
                if (!on) { Fill(rgb, 0, 0, 0); break; }
                (byte r, byte g, byte b)[]? steps = null;
                if (e.UsePalette && TryPalette(e, out var pal) && pal.Length > 0) steps = pal;
                else
                {
                    var b = BoxStops(e, c);
                    if (b.Length > 1) steps = b;
                }
                if (steps is not null)
                {
                    // each blink steps to the next colour
                    long step = (long)Math.Floor(t * speed * 0.8);
                    var (r, g, b2) = steps[((step % steps.Length) + steps.Length) % steps.Length];
                    Fill(rgb, r, g, b2);
                }
                else Fill(rgb, c.r, c.g, c.b);
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
                // Level meter with selectable shape (bar/mirror/pulse) and colouring
                // (two-colour gradient, multi-colour palette, or green→red level ramp).
                // Silence (below the noise gate) shows the background, never the colours.
                var peak = ParseHex(e.ColorHex, e.Brightness);
                var tailC = ParseHex(e.Color2Hex, e.Brightness);
                var bg = ParseBg(e);
                double level = Math.Clamp(BandValue(e.AudioBand, ctx) * e.AudioGain, 0, 1);
                const double gate = 0.02;           // noise floor: below this, everything to background
                if (level <= gate)
                {
                    if (audioState is not null) { audioState.Peak = 0; audioState.LastTime = ctx.Time; }
                    Fill(rgb, bg.r, bg.g, bg.b);
                    break;
                }
                var gradStops = BoxStops(e, peak, tailC); // boxes + extras (== old 2-stop ramp when empty)
                (byte r, byte g, byte b) colorAt(double pos01, double lvl) => e.AudioColor switch                {
                    "palette" when TryPalette(e, out var stops) && stops.Length > 0 => Sample(stops, pos01),
                    "level" => Hsv((1 - Math.Clamp(pos01, 0, 1)) * 0.33, 1.0, e.Brightness),
                    "rainbow" => Hsv(Frac(pos01 * 0.85 + ctx.Time * speed * 0.05), 1.0, e.Brightness),
                    _ => Sample(gradStops, pos01),
                };
                double held = level;
                if (e.PeakHold && audioState is not null)
                {
                    double dt = double.IsNaN(audioState.LastTime) ? 0 : Math.Max(0, ctx.Time - audioState.LastTime);
                    audioState.LastTime = ctx.Time;
                    audioState.Peak = Math.Max(level, audioState.Peak - dt * 0.55); // falls in ~2 s
                    held = Math.Max(level, audioState.Peak);
                }
                if (e.AudioMode == "pulse")
                {
                    // whole strip breathes with the music in one level-coloured hue
                    var (r, g, b) = colorAt(0.5, level);
                    double k = 0.2 + 0.8 * level;
                    Fill(rgb, Scale(r, k), Scale(g, k), Scale(b, k));
                }
                else if (e.AudioMode == "mirror")
                {
                    // center-out: both halves mirror each other, peak dot on both edges
                    int half = ledCount / 2;
                    int litHalf = (int)Math.Round(level * half);
                    for (int i = 0; i < ledCount; i++)
                    {
                        int d = i < half ? half - 1 - i : i - (ledCount - half);
                        int mirrorPos = half - 1 - Math.Min(d, half - 1);
                        if (d < litHalf)
                        {
                            double k = half <= 1 ? 0 : (double)mirrorPos / (half - 1);
                            var (r, g, b) = colorAt(k, level);
                            Set(rgb, i, r, g, b);
                        }
                        else Set(rgb, i, bg.r, bg.g, bg.b);
                    }
                    if (e.PeakHold) PeakDot(rgb, ledCount, held, half, bg, colorAt, mirror: true);
                }
                else
                {
                    // bar — or dots: same fill, but only every 3rd LED lights (dotted meter)
                    bool dots = e.AudioMode == "dots";
                    int lit = (int)Math.Round(level * ledCount);
                    for (int i = 0; i < ledCount; i++)
                    {
                        int pos = dir < 0 ? ledCount - 1 - i : i;
                        if (pos < lit && (!dots || pos % 3 == 0))
                        {
                            double k = lit <= 1 ? 0 : (double)pos / Math.Max(1, lit - 1);
                            var (r, g, b) = colorAt(k, level);
                            Set(rgb, i, r, g, b);
                        }
                        else Set(rgb, i, bg.r, bg.g, bg.b);
                    }
                    if (e.PeakHold) PeakDot(rgb, ledCount, held, lit, bg, colorAt, mirror: false);
                }
                BeatFlash(e, ctx, rgb);
                break;
            }
            case EffectType.Spectrum:
            {
                // Three mini-bars side by side: bass | mid | treble, each its own colour.
                // Silence shows the background; direction reverses the segment order.
                var bg = ParseBg(e);
                double b = Math.Clamp(ctx.AudioBass * e.AudioGain, 0, 1);
                double m = Math.Clamp(ctx.AudioMid * e.AudioGain, 0, 1);
                double tr = Math.Clamp(ctx.AudioTreble * e.AudioGain, 0, 1);
                const double gate = 0.02;
                if (Math.Max(b, Math.Max(m, tr)) <= gate)
                {
                    Fill(rgb, bg.r, bg.g, bg.b);
                    break;
                }
                var cols = new[]
                {
                    ParseHex(e.ColorHex, e.Brightness),
                    ParseHex(e.Color2Hex, e.Brightness),
                    ParseHex(e.Color3Hex, e.Brightness),
                };
                var lvls = new[] { b, m, tr };
                int third = Math.Max(1, ledCount / 3);
                // segment s covers [s*third, min((s+1)*third, ledCount)) — the last one takes the tail
                for (int s = 0; s < 3; s++)
                {
                    int seg = dir < 0 ? 2 - s : s;
                    int start = seg * third;
                    int end = seg == 2 ? ledCount : Math.Min(ledCount, start + third);
                    if (start >= ledCount) break;
                    int n = end - start;
                    int lit = (int)Math.Round(lvls[s] * n);
                    for (int i = 0; i < n; i++)
                    {
                        if (i < lit) Set(rgb, start + i, cols[s].r, cols[s].g, cols[s].b);
                        else Set(rgb, start + i, bg.r, bg.g, bg.b);
                    }
                }
                BeatFlash(e, ctx, rgb);
                break;
            }
            case EffectType.Scanner:
            {
                // KITT-style bounce: the head travels to the end and back (Comet wraps instead).
                var c = ParseHex(e.ColorHex, e.Brightness);
                double span = Math.Max(1, ledCount - 1);
                double cyc = Frac(t * speed * 0.35) * 2;       // 0..2
                double u = cyc < 1 ? cyc : 2 - cyc;            // 0..1..0 triangle
                if (dir < 0) u = 1 - u;
                double head = u * span;   // seed already shifts t, so zones desync
                double tail = Math.Max(3, ledCount * 0.3);
                bool fwd = (cyc < 1) == (dir > 0);
                for (int i = 0; i < ledCount; i++)
                {
                    double d = fwd ? head - i : i - head;      // tail trails behind the motion
                    double k = d >= 0 && d <= tail ? 1.0 - d / tail : 0;
                    k *= k;
                    Set(rgb, i, Scale(c.r, k), Scale(c.g, k), Scale(c.b, k));
                }
                break;
            }
            case EffectType.Sparkle:
            {
                // Dim base colour with deterministic twinkles (hash noise: same frame ⇒ same frame).
                var baseC = ParseHex(e.ColorHex, e.Brightness);
                var spC = ParseHex(e.Color2Hex, e.Brightness);
                long frame = (long)(t * (2 + speed * 10));
                double f = t * (2 + speed * 10) - frame;
                double density = 0.05 + Math.Clamp(e.Speed, 0, 1) * 0.25; // 5..30% LEDs lit
                for (int i = 0; i < ledCount; i++)
                {
                    double n1 = Noise(i + spatial, frame);
                    double n2 = Noise(i + spatial, frame + 7);
                    double tw = n1 * (1 - f) + n2 * f;
                    if (tw > 1 - density)
                    {
                        double k = (tw - (1 - density)) / density;
                        Set(rgb, i, Scale(spC.r, k), Scale(spC.g, k), Scale(spC.b, k));
                    }
                    else Set(rgb, i, Scale(baseC.r, 0.12), Scale(baseC.g, 0.12), Scale(baseC.b, 0.12));
                }
                break;
            }
            case EffectType.Plasma:
            {
                // Flowing blobs over the boxes (+ extras), or the palette when enabled.
                var stops = StopsOrBoxes(e,
                    ParseHex(e.ColorHex, e.Brightness),
                    ParseHex(e.Color2Hex, e.Brightness),
                    ParseHex(e.Color3Hex, e.Brightness));
                for (int i = 0; i < ledCount; i++)
                {
                    double pos = ledCount <= 1 ? 0 : (double)i / (ledCount - 1);
                    if (dir < 0) pos = 1 - pos;
                    double k = Frac(pos * 1.5 + t * speed * 0.15
                                    + 0.25 * Math.Sin(2 * Math.PI * (pos * 2 + t * speed * 0.1)));
                    var (r, g, b) = Sample(stops, k);
                    Set(rgb, i, r, g, b);
                }
                break;
            }
            case EffectType.Custom:
            {
                var pixels = (e.CustomPixels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (pixels.Length == 0) { Fill(rgb, 0, 0, 0); break; }
                var pal = pixels.Select(p => ParseHex(p, e.Brightness)).ToArray();
                int L = pal.Length;
                // Custom indexes the palette with an integer floor, so a time-based per-zone
                // offset can land on a whole multiple of the palette length and make
                // "unsynced" look synced. Use the BASE time plus a palette offset that is
                // guaranteed to be a non-zero residue mod L instead.
                double shift = ctx.Time * speed * 4.0 * dir;
                int zoneShift = seed == 0 || L < 2 ? 0 : 1 + (int)(Math.Abs((long)seed) % (L - 1));
                for (int i = 0; i < ledCount; i++)
                {
                    long raw = (long)Math.Floor(i + (double)zoneShift + shift);
                    int idx = (int)(((raw % L) + L) % L);
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

    /// <summary>Adds a white kick-flash over the whole frame (music effects only).</summary>
    private static void BeatFlash(EffectDef e, EffectContext ctx, byte[] rgb)
    {
        double k = Math.Clamp(ctx.Beat, 0, 1) * Math.Clamp(e.BeatStrength, 0, 1);
        if (k <= 0.01) return;
        byte add = (byte)Math.Round(255 * k);
        for (int i = 0; i < rgb.Length; i++)
        {
            int v = rgb[i] + add;
            rgb[i] = (byte)(v > 255 ? 255 : v);
        }
    }

    private static double SpeedFactor(double s01) => 0.15 + Math.Clamp(s01, 0, 1) * 3.85; // 0..1 -> 0.15..4

    /// <summary>Background colour for music effects: AudioBgHex, or black when empty.</summary>
    private static (byte r, byte g, byte b) ParseBg(EffectDef e) =>
        string.IsNullOrWhiteSpace(e.AudioBgHex) ? ((byte)0, (byte)0, (byte)0) : ParseHex(e.AudioBgHex, e.Brightness);

    /// <summary>Two-colour stops, or the CustomPixels palette when UsePalette is on.</summary>
    private static (byte r, byte g, byte b)[] Stops(EffectDef e, (byte r, byte g, byte b) c1, (byte r, byte g, byte b) c2)
        => e.UsePalette && TryPalette(e, out var pal) && pal.Length > 1 ? pal : new[] { c1, c2 };

    /// <summary>Fixed boxes + user-added ExtraColors stops (empty extras ⇒ just the boxes).</summary>
    private static (byte r, byte g, byte b)[] BoxStops(EffectDef e, params (byte r, byte g, byte b)[] bases)
    {
        if (e.ExtraColors is null || e.ExtraColors.Count == 0) return bases;
        var list = new List<(byte r, byte g, byte b)>(bases);
        foreach (var hx in e.ExtraColors.Take(8))
        {
            var t = (hx ?? "").Trim().TrimStart('#');
            if ((t.Length == 6 || t.Length == 3) && t.All(ch => Uri.IsHexDigit(ch)))
                list.Add(ParseHex(t, e.Brightness));
        }
        return list.Count > bases.Length ? list.ToArray() : bases;
    }

    /// <summary>Palette wins when enabled, otherwise boxes + extras.</summary>
    private static (byte r, byte g, byte b)[] StopsOrBoxes(EffectDef e, params (byte r, byte g, byte b)[] bases)
        => e.UsePalette && TryPalette(e, out var pal) && pal.Length > 1 ? pal : BoxStops(e, bases);

    private static bool TryPalette(EffectDef e, out (byte r, byte g, byte b)[] pal)
    {
        var parts = (e.CustomPixels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { pal = Array.Empty<(byte, byte, byte)>(); return false; }
        pal = parts.Select(p => ParseHex(p, e.Brightness)).ToArray();
        return true;
    }

    /// <summary>Samples a multi-stop palette at 0..1.</summary>
    private static (byte r, byte g, byte b) Sample((byte r, byte g, byte b)[] stops, double k)
    {
        if (stops.Length == 0) return (0, 0, 0);
        if (stops.Length == 1) return stops[0];
        double x = Math.Clamp(k, 0, 1) * (stops.Length - 1);
        int i = Math.Min((int)Math.Floor(x), stops.Length - 2);
        double f = x - i;
        return (Lerp(stops[i].r, stops[i + 1].r, f),
                Lerp(stops[i].g, stops[i + 1].g, f),
                Lerp(stops[i].b, stops[i + 1].b, f));
    }

    /// <summary>Peak-hold dot: a whitened LED at the recent maximum, past the live edge.</summary>
    private static void PeakDot(byte[] rgb, int ledCount, double held, int litEdge,
                                (byte r, byte g, byte b) bg,
                                Func<double, double, (byte r, byte g, byte b)> colorAt, bool mirror)
    {
        static (byte r, byte g, byte b) White((byte r, byte g, byte b) c) =>
            ((byte)(c.r + (255 - c.r) / 2), (byte)(c.g + (255 - c.g) / 2), (byte)(c.b + (255 - c.b) / 2));
        if (mirror)
        {
            int half = ledCount / 2;
            if (half <= 0) return;
            int ph = (int)Math.Round(Math.Clamp(held, 0, 1) * half) - 1;
            if (ph < 0 || ph >= half) return;
            var (r, g, b) = White(colorAt(half <= 1 ? 0 : (double)(half - 1 - ph) / (half - 1), held));
            Set(rgb, half - 1 - ph, r, g, b);
            Set(rgb, (ledCount - half) + ph, r, g, b);
        }
        else
        {
            int dot = (int)Math.Round(Math.Clamp(held, 0, 1) * ledCount) - 1;
            if (dot < 0 || dot >= ledCount) return;
            var (r, g, b) = White(colorAt(litEdge <= 1 ? 0 : (double)dot / Math.Max(1, litEdge - 1), held));
            Set(rgb, dot, r, g, b);
        }
    }

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

using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using ColorConverter = System.Windows.Media.ColorConverter;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace FullRGB;

/// <summary>
/// Runtime theming. The accent colour lives in ONE place (the App.xaml brushes) and is
/// mutated in place, so every control that referenced it — including ones already rendered —
/// updates without rebuilding the UI. Brushes must therefore never be frozen.
/// </summary>
public static class Theme
{
    /// <summary>Accent presets offered in Settings (first entry is the default).</summary>
    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Cyan",   "#00E5FF"),
        ("Violet", "#7C4DFF"),
        ("Pink",   "#FF4D8D"),
        ("Green",  "#3DDC97"),
        ("Amber",  "#FFB01F"),
        ("Red",    "#FF5C6C"),
        ("Blue",   "#4D8DFF"),
        ("White",  "#E8EFF6"),
    };

    public static Color Parse(string hex, Color fallback)
    {
        try
        {
            var s = (hex ?? "").Trim();
            if (!s.StartsWith('#')) s = "#" + s;
            return (Color)ColorConverter.ConvertFromString(s)!;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Repaints every accent-derived brush from a single hex colour.
    /// Brushes that come from BAML are frozen by WPF, so we REPLACE the resource entries
    /// (and every consumer uses DynamicResource) instead of mutating the brush in place —
    /// mutating silently did nothing.
    /// </summary>
    public static void ApplyAccent(string hex)
    {
        var app = Application.Current;
        if (app is null) return;
        var accent = Parse(hex, Color.FromRgb(0x00, 0xE5, 0xFF));

        Set("Accent", new SolidColorBrush(accent));
        // dim: the accent over the app background, used for selected chips/rows
        Set("AccentDim", new SolidColorBrush(Mix(accent, Color.FromRgb(0x08, 0x0A, 0x0F), 0.84)));
        Set("AccentSoft", new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)));
        // text drawn ON the accent fill: dark for bright accents, light for dark ones
        Set("OnAccent", new SolidColorBrush(Luminance(accent) > 0.5
            ? Mix(accent, Colors.Black, 0.85)
            : Color.FromRgb(0xF2, 0xF8, 0xFF)));

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        fill.GradientStops.Add(new GradientStop(Mix(accent, Colors.Black, 0.16), 0));
        fill.GradientStops.Add(new GradientStop(accent, 1));
        Set("AccentFill", fill);

        var brand = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brand.GradientStops.Add(new GradientStop(accent, 0));
        brand.GradientStops.Add(new GradientStop(Rotate(accent, 45), 0.55));
        brand.GradientStops.Add(new GradientStop(Rotate(accent, -45), 1));
        Set("BrandGrad", brand);

        static void Set(string key, Brush brush)
        {
            if (Application.Current is { } a) a.Resources[key] = brush;
        }
    }

    /// <summary>Linear blend: k=0 keeps a, k=1 returns b.</summary>
    public static Color Mix(Color a, Color b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * k),
            (byte)Math.Round(a.G + (b.G - a.G) * k),
            (byte)Math.Round(a.B + (b.B - a.B) * k));
    }

    public static double Luminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>Hue-rotates a colour, keeping saturation and value — used for the brand triad.</summary>
    public static Color Rotate(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double v = max, d = max - min;
        double s = max <= 0 ? 0 : d / max;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        h = (h + degrees / 360.0) % 1.0;
        if (h < 0) h += 1;
        var (rr, gg, bb) = Effects.EffectRenderer.Hsv(h, s, v);
        return Color.FromRgb(rr, gg, bb);
    }
}

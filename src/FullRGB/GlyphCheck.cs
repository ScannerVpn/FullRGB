using System.Windows.Media;

namespace FullRGB;

/// <summary>
/// Verifies that every icon codepoint the UI uses actually exists in Segoe MDL2 Assets.
/// A missing glyph renders as a hollow "tofu" box, which is what made several buttons
/// look broken; the compiler cannot catch it, so it is a test instead.
/// </summary>
public static class GlyphCheck
{
    /// <summary>All MDL2 codepoints referenced from XAML and code, with where they are used.</summary>
    public static readonly (string Where, string Glyph)[] Used =
    {
        ("titlebar.min", "\uE921"),
        ("titlebar.max", "\uE922"),
        ("titlebar.close", "\uE8BB"),
        ("statuspill.refresh", "\uE72C"),
        ("banner.icue", "\uE7BA"),
        ("banner.smbus", "\uE946"),
        ("nav.lighting", "\uE781"),
        ("nav.devices", "\uE977"),
        ("nav.hardware", "\uE7F4"),
        ("nav.settings", "\uE713"),
        ("combo.chevron", "\uE70D"),
        ("expander.collapsed", "\uE76C"),
        ("row.menu", "\uE712"),
        ("row.all", "\uE80F"),
        ("profile.rename", "\uE70F"),
        ("profile.new", "\uE710"),
        ("profile.delete", "\uE74D"),
        ("accent.custom", "\uE790"),
        ("zone.identify", "\uE781"),
        ("dev.motherboard", "\uE964"),
        ("dev.dram", "\uE950"),
        ("dev.gpu", "\uE7F4"),
        ("dev.cooler", "\uEA80"),
        ("dev.strip", "\uE781"),
        ("dev.keyboard", "\uE92E"),
        ("dev.mouse", "\uE962"),
        ("dev.case", "\uE7F8"),
        ("dev.storage", "\uEDA2"),
        ("dev.other", "\uE770"),
        ("titlebar.restore", "\uE923"),
        ("accent.check", "\uE73E"),
    };

    /// <summary>Returns the entries whose codepoint is NOT in the font.</summary>
    public static List<string> Missing()
    {
        var bad = new List<string>();
        var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                                   System.Windows.FontStyles.Normal,
                                   System.Windows.FontWeights.Normal,
                                   System.Windows.FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var gt))
            return new List<string> { "Segoe MDL2 Assets is not installed" };

        // effect tiles come from the live catalog, so a new effect cannot skip this check
        foreach (var (where, glyph) in Used.Concat(MainWindow.CatalogGlyphs))
        {
            int cp = char.ConvertToUtf32(glyph, 0);
            if (!gt.CharacterToGlyphMap.ContainsKey(cp))
                bad.Add($"{where} (U+{cp:X4})");
        }
        return bad;
    }
}

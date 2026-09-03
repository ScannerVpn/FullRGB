using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FullRGB.Config;
using FullRGB.Effects;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Slider = System.Windows.Controls.Slider;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace FullRGB;

/// <summary>Effect chooser, parameter editor and the animated hero preview.</summary>
public partial class MainWindow
{
    /// <summary>
    /// One entry per effect. <c>Glyph</c> is an MDL2 codepoint; <c>Path</c> is vector path data
    /// used where the font has no suitable icon (MDL2 has no flame, comet, sine wave or rainbow,
    /// and U+E9CB — the old "temperature" glyph — does not exist at all and rendered as a box).
    /// Exactly one of the two is set; <c>Filled</c> paths are filled, others stroked.
    /// </summary>
    private readonly record struct FxIcon(EffectType T, string Key, string? Glyph,
                                         string? Path = null, bool Filled = false);

    private static readonly FxIcon[] FxCatalog =
    {
        new(EffectType.Solid,       "chip.solid",     "\uE91F"),
        new(EffectType.Gradient,    "chip.gradient",  null, GradientPath, true),
        new(EffectType.Rainbow,     "chip.rainbow",   null, RainbowPath),
        new(EffectType.ColorCycle,  "chip.cycle",     "\uE895"),
        new(EffectType.Breathing,   "chip.breathing", "\uE9A9"),
        new(EffectType.Wave,        "chip.wave",      null, WavePath),
        new(EffectType.Comet,       "chip.comet",     null, CometPath, true),
        new(EffectType.Blink,       "chip.blink",     "\uE945"),
        new(EffectType.Fire,        "chip.fire",      null, FirePath, true),
        new(EffectType.Temperature, "chip.temp",      "\uE9CA"),
        new(EffectType.AudioVU,     "chip.audio",     "\uE8D6"),
        new(EffectType.Custom,      "chip.custom",    "\uE70F"),
        new(EffectType.Spectrum,    "chip.spectrum",  null, SpectrumPath, true),
        new(EffectType.Scanner,     "chip.scanner",   null, ScannerPath),
        new(EffectType.Sparkle,     "chip.sparkle",   null, SparklePath, true),
        new(EffectType.Plasma,      "chip.plasma",    null, PlasmaPath),
    };

    // 24×24 icon geometries (drawn to match MDL2's optical weight)
    private const string FirePath =
        "M12 2 C13 6 17 7.5 17 12 C17 15.9 14.8 18 12 18 C9.2 18 7 15.9 7 12 " +
        "C7 9.6 8.4 8.4 9.4 7.2 C9.3 9.2 10 10.2 11 10.6 C11.4 8 10.6 5.2 12 2 Z " +
        "M12 16.6 C13.4 16.6 14.4 15.4 14.4 13.9 C14.4 12.2 13.2 11.3 12.6 9.9 " +
        "C12.2 11.4 11 12 10.4 13 C10 13.7 9.9 14.2 9.9 14.5 C9.9 15.8 10.8 16.6 12 16.6 Z";
    private const string CometPath =
        "M17.5 4.2 A3.2 3.2 0 1 1 17.49 4.2 Z " +
        "M14.4 7.3 L4 17.7 L3.2 20.8 L6.3 20 L16.7 9.6 C16 9.2 15 8.2 14.4 7.3 Z";
    private const string WavePath =
        "M2 12 C4 5.5 7 5.5 9 12 C11 18.5 14 18.5 16 12 C17.2 8.1 18.9 6.5 21 7.4";
    private const string RainbowPath =
        "M2.5 19 A9.5 9.5 0 0 1 21.5 19 M6 19 A6 6 0 0 1 18 19 M9.5 19 A2.5 2.5 0 0 1 14.5 19";
    private const string GradientPath =
        "M3 5 H21 V9 H3 Z M3 10.4 H21 V13.6 H3 Z M3 15 H21 V18 H3 Z";
    private const string SpectrumPath =
        "M4 20 V11 H7 V20 Z M10.5 20 V4 H13.5 V20 Z M17 20 V13 H20 V20 Z";
    private const string ScannerPath =
        "M3 12 H21 M17 7 L22 12 L17 17 M7 7 L2 12 L7 17";
    private const string SparklePath =
        "M12 2.5 L14.2 9.8 L21.5 12 L14.2 14.2 L12 21.5 L9.8 14.2 L2.5 12 L9.8 9.8 Z";
    private const string PlasmaPath =
        "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M7.5 12 C9.5 8.5 10.5 15.5 12.5 12 C14 9.5 15.5 10.5 16.5 12";

    /// <summary>Effect types that have a chip in the picker (UI-test hook).</summary>
    internal static IReadOnlyCollection<EffectType> CatalogTypes =>
        FxCatalog.Select(f => f.T).ToHashSet();

    /// <summary>MDL2 codepoints used by effect tiles (glyph-existence test hook).</summary>
    internal static IEnumerable<(string Where, string Glyph)> CatalogGlyphs =>
        FxCatalog.Where(f => f.Glyph is not null).Select(f => (f.Key, f.Glyph!));

    private void BuildEffectEditor()
    {
        EffectChips.Children.Clear();
        EffectParams.Children.Clear();

        EffectHdr.Text = L10n.T("section.effect");
        UpdateHeroCaption();

        foreach (var icon in FxCatalog)
        {
            var col = new StackPanel();
            col.Children.Add(FxIconVisual(icon));
            col.Children.Add(new TextBlock
            {
                Text = L10n.T(icon.Key),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var rb = new RadioButton
            {
                GroupName = "fx",
                Style = (Style)FindResource("FxTile"),
                Content = col,
                Tag = icon.T,
                IsChecked = _edit.Type == icon.T,
                ToolTip = L10n.T(icon.Key),
            };
            var t = icon.T;
            rb.Checked += (_, _) =>
            {
                _edit.Type = t;
                BuildParams();
                UpdateHeroCaption();
                PushEdit();
            };
            EffectChips.Children.Add(rb);
        }
        BuildParams();
    }

    /// <summary>Renders a catalog entry as either an MDL2 glyph or a vector path, same footprint.</summary>
    private FrameworkElement FxIconVisual(FxIcon icon)
    {
        if (icon.Glyph is not null)
            return new TextBlock
            {
                Text = icon.Glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Height = 21,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 6),
            };

        var geo = Geometry.Parse(icon.Path!);
        var path = new System.Windows.Shapes.Path
        {
            Data = geo,
            Stretch = Stretch.Uniform,
            Width = 17,
            Height = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 6),
            // The tile's Foreground is set by the FxTile template's triggers, so bind to it and
            // the icon follows selection/hover exactly like the glyph tiles do.
        };
        path.SetBinding(icon.Filled ? System.Windows.Shapes.Path.FillProperty
                                    : System.Windows.Shapes.Path.StrokeProperty,
            new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1),
            });
        if (!icon.Filled)
        {
            path.StrokeThickness = 1.9;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;
        }
        var host = new Grid { Height = 21 };
        host.Children.Add(path);
        return host;
    }

    /// <summary>The hero card names the effect and what it is applied to.</summary>
    private void UpdateHeroCaption()
    {
        var entry = FxCatalog.FirstOrDefault(f => f.T == _edit.Type);
        HeroFxTxt.Text = entry.Key is null ? "" : L10n.T(entry.Key);

        var dev = _client?.Controllers.FirstOrDefault(c => c.Key == _selectedKey);
        HeroSubTxt.Text = _target switch
        {
            TargetMode.Zone when dev is not null =>
                ShortName(dev.Name) + " · " +
                (dev.Zones.FirstOrDefault(z => z.Index == _selectedZone)?.Name ?? ""),
            TargetMode.Device when dev is not null => ShortName(dev.Name),
            _ => L10n.T("hero.global"),
        };
    }

    /// <summary>
    /// True when <see cref="EffectRenderer"/> reads <c>EffectDef.ColorHex</c> for this effect.
    /// Kept next to the parameter form (and asserted in --rendertest) so the UI cannot drift
    /// away from what the renderer does.
    /// </summary>
    internal static bool UsesPrimaryColor(EffectType t) => t switch
    {
        EffectType.Rainbow => false,      // generates hues across the strip
        EffectType.ColorCycle => false,   // generates one rotating hue
        EffectType.Fire => false,         // fixed ember palette
        EffectType.Temperature => false,  // fixed cold -> hot ramp
        EffectType.Custom => false,       // palette comes from CustomPixels
        _ => true,
    };

    private void BuildParams()
    {
        EffectParams.Children.Clear();

        // Preset gallery: one click fills the colours + the palette (proper nouns, no l10n needed).
        // Shows the preset the current colours match, or nothing when customised.
        if (UsesPrimaryColor(_edit.Type) || _edit.Type == EffectType.Custom)
        {
            var names = EffectPresets.All.Select(p => p.Name).ToArray();
            int matched = -1;
            for (int i = 0; i < EffectPresets.All.Length; i++)
            {
                var c = EffectPresets.All[i].Colors;
                if (SameHex(_edit.ColorHex, c[0]) && SameHex(_edit.Color2Hex, c[1]) && SameHex(_edit.Color3Hex, c[2]))
                { matched = i; break; }
            }
            var presetCmb = Combo(names, matched, i =>
            {
                if (i < 0 || i >= names.Length) return;
                EffectPresets.Apply(_edit, EffectPresets.All[i]);
                BuildParams();
                PushEdit();
            });
            presetCmb.ToolTip = L10n.T("lbl.preset");
            AddRow(L10n.T("lbl.preset"), presetCmb);
        }

        // Only show the primary colour when the renderer actually READS it. Rainbow, ColorCycle
        // and Fire generate their own hues, Temperature uses a fixed cold→hot ramp, and Custom
        // takes a palette list instead — showing "#00E5FF" next to a rainbow preview told the
        // user a colour was in effect when it was not.
        if (UsesPrimaryColor(_edit.Type))
            AddRow(L10n.T("lbl.color"), ColorBox(_edit.ColorHex, c => _edit.ColorHex = c));

        if (_edit.Type is EffectType.Wave or EffectType.AudioVU or EffectType.Gradient
            or EffectType.Spectrum or EffectType.Plasma)
            AddRow(L10n.T("lbl.color2"), ColorBox(_edit.Color2Hex, c => _edit.Color2Hex = c));

        if (_edit.Type is EffectType.Spectrum or EffectType.Plasma)
            AddRow(L10n.T("lbl.color3"), ColorBox(_edit.Color3Hex, c => _edit.Color3Hex = c));

        // Extra stops: whoever wants more colours taps + and the gradient-style effects
        // sample them all (Gradient, Wave, Breathing, Blink, Comet, Plasma, Music-gradient).
        if (_edit.Type is EffectType.Gradient or EffectType.Wave or EffectType.Breathing
            or EffectType.Blink or EffectType.Comet or EffectType.Plasma or EffectType.AudioVU)
            AddRow(L10n.T("lbl.extracolors"), ExtraColorsEditor());

        // Speed only exists for effects that actually move (Spectrum follows the music, not time).
        if (_edit.Type is EffectType.Rainbow or EffectType.ColorCycle or EffectType.Breathing
            or EffectType.Wave or EffectType.Comet or EffectType.Blink or EffectType.Fire or EffectType.Custom
            or EffectType.Scanner or EffectType.Sparkle or EffectType.Plasma)
            AddSlider(L10n.T("lbl.speed"), _edit.Speed, v => _edit.Speed = v);

        AddSlider(L10n.T("lbl.brightness"), _edit.Brightness, v => _edit.Brightness = v);

        if (_edit.Type == EffectType.Temperature)
        {
            var cmb = Combo(new[] { L10n.T("sensor.cpu"), L10n.T("sensor.gpu") },
                            _edit.TempSensor == "gpu" ? 1 : 0,
                            i => { _edit.TempSensor = i == 1 ? "gpu" : "cpu"; PushEdit(); });
            AddRow(L10n.T("lbl.sensor"), cmb);
            AddSlider(L10n.T("lbl.tlow"), (_edit.TempLow - 20) / 70.0, v => _edit.TempLow = 20 + v * 70,
                      () => $"{_edit.TempLow:F0}°C");
            AddSlider(L10n.T("lbl.thigh"), (_edit.TempHigh - 40) / 60.0, v => _edit.TempHigh = 40 + v * 60,
                      () => $"{_edit.TempHigh:F0}°C");
        }

        if (_edit.Type == EffectType.AudioVU)
        {
            var bands = new[] { "level", "bass", "mid", "treble" };
            var cmb = Combo(new[] { L10n.T("band.level"), L10n.T("band.bass"), L10n.T("band.mid"), L10n.T("band.treble") },
                            Math.Max(0, Array.IndexOf(bands, _edit.AudioBand)),
                            i => { _edit.AudioBand = bands[Math.Clamp(i, 0, 3)]; PushEdit(); });
            AddRow(L10n.T("lbl.band"), cmb);

            var modes = new[] { "bar", "mirror", "pulse", "dots" };
            var modeCmb = Combo(new[] { L10n.T("mode.bar"), L10n.T("mode.mirror"), L10n.T("mode.pulse"), L10n.T("mode.dots") },
                            Math.Max(0, Array.IndexOf(modes, _edit.AudioMode)),
                            i => { _edit.AudioMode = modes[Math.Clamp(i, 0, 3)]; PushEdit(); });
            AddRow(L10n.T("lbl.audiomode"), modeCmb);

            var colModes = new[] { "gradient", "palette", "level", "rainbow" };
            var colCmb = Combo(new[] { L10n.T("colormode.gradient"), L10n.T("colormode.palette"), L10n.T("colormode.level"), L10n.T("colormode.rainbow") },
                            Math.Max(0, Array.IndexOf(colModes, _edit.AudioColor)),
                            i => { _edit.AudioColor = colModes[Math.Clamp(i, 0, 3)]; PushEdit(); });
            AddRow(L10n.T("lbl.audiocolor"), colCmb);

            AddCheck(L10n.T("lbl.peak"), _edit.PeakHold, v => _edit.PeakHold = v);
            AddBackgroundRow();
            AddSlider(L10n.T("lbl.sensitivity"), (_edit.AudioGain - 0.2) / 2.3, v => _edit.AudioGain = 0.2 + v * 2.3,
                      () => $"{_edit.AudioGain:F1}×");
            AddSlider(L10n.T("lbl.beat"), _edit.BeatStrength, v => _edit.BeatStrength = v);
            if (_edit.AudioColor == "palette")
                AddRow(L10n.T("lbl.pixels"), PaletteEditor());
            if (_audioFailed)
                EffectParams.Children.Add(new TextBlock
                {
                    Text = L10n.T("fx.audioOff"),
                    Style = (Style)FindResource("FaintTxt"),
                    Foreground = (Brush)FindResource("Warn"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                });
        }

        if (_edit.Type == EffectType.Spectrum)
        {
            AddBackgroundRow();
            AddSlider(L10n.T("lbl.sensitivity"), (_edit.AudioGain - 0.2) / 2.3, v => _edit.AudioGain = 0.2 + v * 2.3,
                      () => $"{_edit.AudioGain:F1}×");
            AddSlider(L10n.T("lbl.beat"), _edit.BeatStrength, v => _edit.BeatStrength = v);
        }

        if (_edit.Type is EffectType.Wave or EffectType.Breathing or EffectType.Blink
            or EffectType.Comet or EffectType.Gradient)
        {
            AddCheck(L10n.T("lbl.usepalette"), _edit.UsePalette, v => _edit.UsePalette = v);
            if (_edit.UsePalette)
                AddRow(L10n.T("lbl.pixels"), PaletteEditor());
        }

        if (_edit.Type == EffectType.Custom)
        {
            AddRow(L10n.T("lbl.pixels"), PaletteEditor());
        }

        if (_edit.Type is EffectType.Wave or EffectType.Custom or EffectType.AudioVU
            or EffectType.Comet or EffectType.Gradient or EffectType.Rainbow
            or EffectType.Spectrum or EffectType.Plasma or EffectType.Scanner)
        {
            var cmb = Combo(new[] { L10n.T("dir.forward"), L10n.T("dir.reverse") },
                            _edit.Direction == "reverse" ? 1 : 0,
                            i => { _edit.Direction = i == 1 ? "reverse" : "forward"; PushEdit(); });
            AddRow(L10n.T("lbl.direction"), cmb);
        }

        // Sync: identical phase on every zone (one colour everywhere) vs. offset per zone.
        // Rendered through AddRow so it keeps the label-left / control-right rhythm of the
        // rows above it instead of inverting the form.
        AddCheck(L10n.T("lbl.sync"), _edit.SyncZones, v => _edit.SyncZones = v, L10n.T("lbl.syncHint"));
    }

    private void AddCheck(string label, bool value, Action<bool> set, string? tooltip = null)
    {
        var chk = new CheckBox
        {
            Style = (Style)FindResource("Chk"),
            IsChecked = value,
            Margin = new Thickness(0),
        };
        if (tooltip is not null) chk.ToolTip = tooltip;
        chk.Checked += (_, _) => { set(true); PushEdit(); };
        chk.Unchecked += (_, _) => { set(false); PushEdit(); };
        AddRow(label, chk);
    }

    /// <summary>Background colour for unlit music LEDs: off (black) or a chosen colour.</summary>
    private void AddBackgroundRow()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var chk = new CheckBox
        {
            Style = (Style)FindResource("Chk"),
            IsChecked = !string.IsNullOrWhiteSpace(_edit.AudioBgHex),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var box = ColorBox(string.IsNullOrWhiteSpace(_edit.AudioBgHex) ? "#000000" : _edit.AudioBgHex,
                           c => _edit.AudioBgHex = c);
        box.IsEnabled = chk.IsChecked == true;
        chk.Checked += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_edit.AudioBgHex)) _edit.AudioBgHex = "#16202E";
            box.IsEnabled = true;
            PushEdit();
        };
        chk.Unchecked += (_, _) => { _edit.AudioBgHex = ""; box.IsEnabled = false; PushEdit(); };
        panel.Children.Add(chk);
        panel.Children.Add(box);
        AddRow(L10n.T("lbl.usebg"), panel);
    }

    /// <summary>Visual palette editor: swatches (click = change, right-click = remove) + add, up to 8.</summary>
    private UIElement PaletteEditor()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var colors = (_edit.CustomPixels ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8).ToList();

        void Refresh(string updated)
        {
            _edit.CustomPixels = updated;
            PushEdit();
            BuildParams(); // rebuild the form so the swatches match the string
        }

        foreach (var hx in colors)
        {
            string current = hx;
            var sw = new Border
            {
                Width = 34, Height = 22, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Theme.Parse(current, Color.FromRgb(0, 229, 255))),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = L10n.T("pal.edit"),
            };
            sw.MouseLeftButtonUp += (_, _) =>
            {
                var picked = ColorPickerDialog.Pick(this, current);
                if (picked is null) return;
                var list = (_edit.CustomPixels ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                int at = list.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (at < 0) return;
                list[at] = picked.TrimStart('#');
                Refresh(string.Join(",", list));
            };
            sw.MouseRightButtonUp += (_, _) =>
            {
                var list = (_edit.CustomPixels ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(x => !x.Equals(current, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Refresh(string.Join(",", list));
            };
            wrap.Children.Add(sw);
        }

        if (colors.Count < 8)
        {
            var add = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = L10n.T("pal.add"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
            };
            add.Click += (_, _) =>
            {
                var picked = ColorPickerDialog.Pick(this, "#00E5FF");
                if (picked is null) return;
                var list = (_edit.CustomPixels ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(7).ToList();
                list.Add(picked.TrimStart('#'));
                Refresh(string.Join(",", list));
            };
            wrap.Children.Add(add);
        }
        return wrap;
    }

    /// <summary>Extra gradient stops: swatches (click = change, right-click = remove) + add, up to 8.</summary>
    private UIElement ExtraColorsEditor()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        _edit.ExtraColors ??= new List<string>();
        var items = _edit.ExtraColors.Take(8).ToList();

        void Refresh()
        {
            PushEdit();
            BuildParams(); // rebuild the form so the swatches match the list
        }

        foreach (var hx in items)
        {
            string current = hx;
            var sw = new Border
            {
                Width = 34, Height = 22, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Theme.Parse(current, Color.FromRgb(0, 229, 255))),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = L10n.T("pal.edit"),
            };
            sw.MouseLeftButtonUp += (_, _) =>
            {
                var picked = ColorPickerDialog.Pick(this, current);
                if (picked is null) return;
                var list = _edit.ExtraColors ??= new List<string>();
                int at = list.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase));
                if (at < 0) return;
                list[at] = picked;
                Refresh();
            };
            sw.MouseRightButtonUp += (_, _) =>
            {
                var list = _edit.ExtraColors ??= new List<string>();
                list.RemoveAll(x => x.Equals(current, StringComparison.OrdinalIgnoreCase));
                Refresh();
            };
            wrap.Children.Add(sw);
        }

        if (items.Count < 8)
        {
            var add = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = L10n.T("pal.add"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
            };
            add.Click += (_, _) =>
            {
                var picked = ColorPickerDialog.Pick(this, "#00E5FF");
                if (picked is null) return;
                var list = _edit.ExtraColors ??= new List<string>();
                while (list.Count >= 8) list.RemoveAt(list.Count - 1);
                list.Add(picked);
                Refresh();
            };
            wrap.Children.Add(add);
        }
        return wrap;
    }

    private static bool SameHex(string a, string b) =>
        a.Trim().TrimStart('#').Equals(b.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase);

    private ComboBox Combo(string[] items, int selected, Action<int> onChange)
    {
        var cmb = new ComboBox { Style = (Style)FindResource("Cmb"), MinWidth = 130 };
        foreach (var i in items) cmb.Items.Add(i);
        cmb.SelectedIndex = selected < 0 ? -1 : Math.Clamp(selected, 0, items.Length - 1);
        cmb.SelectionChanged += (_, _) => onChange(cmb.SelectedIndex);
        return cmb;
    }

    // ---------- hero preview ----------

    private System.Windows.Threading.DispatcherTimer? _previewTimer;
    private WriteableBitmap? _previewBmp;
    private readonly System.Diagnostics.Stopwatch _previewClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly AudioState _previewAudio = new(); // peak-hold memory for the hero preview
    private const int PreviewLeds = 128;
    private const int PreviewRows = 3;
    private byte[]? _previewPixels;

    /// <summary>
    /// Animated strip in the hero card, rendered with the SAME EffectRenderer the engine
    /// uses, so the preview can never disagree with the hardware. One timer for the whole
    /// window lifetime (the old code created a new one on every editor rebuild).
    /// </summary>
    private void StartPreview()
    {
        if (_previewTimer is not null) return;
        _previewBmp = new WriteableBitmap(PreviewLeds, PreviewRows, 96, 96, PixelFormats.Bgr32, null);
        _previewPixels = new byte[PreviewLeds * PreviewRows * 4];
        PreviewImg.Source = _previewBmp;
        RenderOptions.SetBitmapScalingMode(PreviewImg, BitmapScalingMode.Linear);

        _previewTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50), // 20 fps is plenty for a preview
        };
        _previewTimer.Tick += (_, _) => TickPreview();
        _previewTimer.Start();
    }

    private void StopPreview()
    {
        _previewTimer?.Stop();
        _previewTimer = null;
    }

    private void TickPreview()
    {
        if (_previewBmp is null || _previewPixels is null) return;
        if (!IsVisible) return;   // hidden in tray: don't burn CPU

        var ctx = new EffectContext
        {
            Time = _previewClock.Elapsed.TotalSeconds,
            CpuTemp = _engine?.CpuTemp,
            GpuTemp = _engine?.GpuTemp,
            AudioLevel = _audio?.Level ?? 0,
            AudioBass = _audio?.Bass ?? 0,
            AudioMid = _audio?.Mid ?? 0,
            AudioTreble = _audio?.Treble ?? 0,
            Beat = _audio?.Beat ?? 0,
        };
        var rgb = EffectRenderer.Render(_edit, PreviewLeds, 0, ctx, _previewAudio);
        for (int i = 0; i < PreviewLeds; i++)
        {
            byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            for (int y = 0; y < PreviewRows; y++)
            {
                int o = (y * PreviewLeds + i) * 4;
                _previewPixels[o] = b;
                _previewPixels[o + 1] = g;
                _previewPixels[o + 2] = r;
                _previewPixels[o + 3] = 255;
            }
        }
        _previewBmp.WritePixels(new Int32Rect(0, 0, PreviewLeds, PreviewRows),
                                _previewPixels, PreviewLeds * 4, 0);
        if (TabSettings.IsChecked == true) UpdateDiagnostics();
    }

    // ---------- parameter widgets ----------

    private UIElement ColorBox(string hex, Action<string> set)
    {
        var swatch = new Border
        {
            Width = 54, Height = 22, CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Theme.Parse(hex, Color.FromRgb(0, 229, 255))),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
        };
        var label = new TextBlock
        {
            Text = hex.ToUpperInvariant(),
            Style = (Style)FindResource("FaintTxt"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatch);
        content.Children.Add(label);

        var btn = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 5, 12, 5),
        };
        string current = hex;
        btn.Click += (_, _) =>
        {
            var picked = ColorPickerDialog.Pick(this, current);
            if (picked is null) return;
            current = picked;
            set(picked);
            swatch.Background = new SolidColorBrush(Theme.Parse(picked, Color.FromRgb(0, 229, 255)));
            label.Text = picked.ToUpperInvariant();
            PushEdit();
        };
        return btn;
    }

    private void AddRow(string label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 11) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("MutedTxt"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        // Every control column stretches to the same right edge; before this, colour pills,
        // sliders and combo boxes each ended at a different x and the form looked ragged.
        if (control is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Stretch;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        EffectParams.Children.Add(g);
    }

    /// <summary>Slider row with a live numeric readout on the right.</summary>
    private void AddSlider(string label, double v01, Action<double> set, Func<string>? format = null)
    {
        var sl = new Slider
        {
            Minimum = 0, Maximum = 1, Value = Math.Clamp(v01, 0, 1),
            Style = (Style)FindResource("Slider"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Style = (Style)FindResource("FaintTxt"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 38,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
            Text = format?.Invoke() ?? $"{sl.Value * 100:F0}%",
        };
        sl.ValueChanged += (_, _) =>
        {
            set(sl.Value);
            readout.Text = format?.Invoke() ?? $"{sl.Value * 100:F0}%";
            PushEdit();
        };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(sl);
        Grid.SetColumn(readout, 1);
        g.Children.Add(readout);

        AddRow(label, g);
    }

    /// <summary>Writes the edited effect back to the right slot and restarts painting.</summary>
    private void PushEdit()
    {
        if (_loadingUi) return;
        var profile = CurrentProfile();
        var dev = _client?.Controllers.FirstOrDefault(c => c.Key == _selectedKey);
        switch (_target)
        {
            case TargetMode.Zone when dev is not null:
                var zone = dev.Zones.FirstOrDefault(z => z.Index == _selectedZone);
                if (zone is not null)
                    profile.ZoneOverrides[Profile.ZoneKey(dev, zone)] = EffectEngine.Clone(_edit);
                break;
            case TargetMode.Device when _selectedKey is not null:
                profile.DeviceOverrides[_selectedKey] = EffectEngine.Clone(_edit);
                break;
            default:
                profile.GlobalEffect = EffectEngine.Clone(_edit);
                break;
        }
        _engine?.Apply(profile);
        SyncRunButtons();
    }
}

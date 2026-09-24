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
using Ellipse = System.Windows.Shapes.Ellipse;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;
using Brushes = System.Windows.Media.Brushes;
using DropShadowEffect = System.Windows.Media.Effects.DropShadowEffect;

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
        new(EffectType.Ambient,     "chip.ambient",   "\uE7F4"),   // Connected: screen mirror
        new(EffectType.Gaming,      "chip.gaming",    "\uE7FC"),   // Game controller
        new(EffectType.GamePulse,   "chip.gamepulse", null, GamePulsePath),   // ECG pulse, font-independent
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
    private const string GamePulsePath =
        "M2 12 H8 L10.5 6 L13.5 18 L15.5 12 H22";

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

        EffectHdr.Text = L10n.T("effects.title");
        UpdateHeroCaption();
        RefreshStats();

        foreach (var icon in FxCatalog)
        {
            var col = new StackPanel();
            col.Children.Add(BuildEffectArt(icon.T));

            // Name row: localised name over the English one (only when they differ), with the
            // arena's round check badge on the selected tile and its icon on the rest.
            var label = new Grid { Margin = new Thickness(10, 0, 10, 10) };
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var names = new StackPanel();
            names.Children.Add(new TextBlock
            {
                Text = L10n.T(icon.Key),
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            // The English line only earns its place when the current language is not English.
            string english = L10n.T(icon.Key, "en");
            if (english != L10n.T(icon.Key))
                names.Children.Add(new TextBlock
                {
                    Text = english,
                    FontSize = 8,
                    Foreground = (Brush)FindResource("Faint"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            Grid.SetColumn(names, 0);
            label.Children.Add(names);

            var mark = new Grid { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (_edit.Type == icon.T)
            {
                mark.Children.Add(new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromRgb(0xBC, 0x95, 0xF3)),
                    Child = new TextBlock
                    {
                        Text = "\uE73E",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x15, 0x2F)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }
            else
            {
                mark.Children.Add(FxIconVisual(icon, 13));
            }
            Grid.SetColumn(mark, 1);
            label.Children.Add(mark);

            col.Children.Add(label);

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
                RefreshStats();
                BuildEffectEditor();
            };
            EffectChips.Children.Add(rb);
        }
        BuildParams();
    }

    /// <summary>Renders a catalog entry as either an MDL2 glyph or a vector path, same footprint.</summary>
    private FrameworkElement FxIconVisual(FxIcon icon, double box = 17)
    {
        if (icon.Glyph is not null)
            return new TextBlock
            {
                Text = icon.Glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = box,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(icon.Path!),
            Stretch = Stretch.Uniform,
            Width = box,
            Height = box,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
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
        return path;
    }

    // ---------- chip art ----------
    //
    // The arena tile art: a LED strip tilted -10°, cut into 16 visible cells, with a specular
    // line along the top and a coloured glow, recoloured per effect. Fire, Comet, Music and
    // Custom break the pattern there too, so they get their own silhouette here.

    private const double ArtHeight = 46;

    private static Color Rgb(int hex) =>
        Color.FromRgb((byte)(hex >> 16 & 0xFF), (byte)(hex >> 8 & 0xFF), (byte)(hex & 0xFF));

    private static Brush Solid(int hex)
    {
        var b = new SolidColorBrush(Rgb(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Horizontal multi-stop gradient; stops are spread evenly across 0..1.</summary>
    private static Brush Grad(params Color[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i < stops.Length; i++)
            b.GradientStops.Add(new GradientStop(stops[i], stops.Length == 1 ? 0 : i / (double)(stops.Length - 1)));
        b.Freeze();
        return b;
    }

    private static DropShadowEffect Glow(Color color, double radius, double opacity) => new()
    {
        Color = color, BlurRadius = radius, ShadowDepth = 0, Opacity = opacity,
    };

    private static DropShadowEffect Glow(int hex, double radius, double opacity) => Glow(Rgb(hex), radius, opacity);

    private readonly record struct StripSpec(double Height, Brush Fill, Color Glow, double GlowOpacity);

    /// <summary>Per-effect strip colours, copied from the arena art-* rules.</summary>
    private static StripSpec StripFor(EffectType t) => t switch
    {
        EffectType.Solid => new StripSpec(6, Solid(0xB881F5), Rgb(0x9E65F1), 0.50),
        EffectType.Breathing => new StripSpec(5, Grad(Rgb(0x744292), Rgb(0xDFB2FF), Rgb(0x8A53A9)), Rgb(0xA469DF), 0.46),
        EffectType.Wave => new StripSpec(6, Grad(Rgb(0x683897), Rgb(0xB58CEF), Rgb(0x6899F0), Rgb(0x2D637C)), Rgb(0x727BFF), 0.39),
        EffectType.Gradient => new StripSpec(6, Grad(Rgb(0x755BE4), Rgb(0xA282F4), Rgb(0xD78CD4), Rgb(0xF3A1C8)), Rgb(0xD88DFF), 0.53),
        EffectType.ColorCycle => new StripSpec(6, Grad(Rgb(0xAD7FFC), Rgb(0xD59EED), Rgb(0xAC81F7), Rgb(0x8189EB)), Rgb(0xAD7FFF), 0.50),
        EffectType.Temperature => new StripSpec(6, Grad(Rgb(0x51C4F3), Rgb(0x68CDC8), Rgb(0x70D5B6), Rgb(0xDBBA61), Rgb(0xEC7C69)), Rgb(0x6BBEA8), 0.33),
        EffectType.Blink => new StripSpec(6, BlinkBrush(), Rgb(0xA178EB), 0.39),
        // Rainbow plus everything the reference leaves on the default palette.
        _ => new StripSpec(6, Grad(Rgb(0xE86674), Rgb(0xEDC670), Rgb(0x70D7BB), Rgb(0x69BCF4), Rgb(0xB682ED)), Rgb(0xA969FB), 0.40),
    };

    /// <summary>Blink's repeating gradient: ~7px lit, then ~9px dark, tiled along the strip.</summary>
    private static Brush BlinkBrush()
    {
        const double period = 16;
        // Built by hand rather than through Grad(): the stops need custom offsets, and Grad()
        // hands back a frozen brush that cannot be touched afterwards.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(Rgb(0xCBC0FF), 0));
        brush.GradientStops.Add(new GradientStop(Rgb(0xBEB0FF), 6 / period));
        brush.GradientStops.Add(new GradientStop(Rgb(0x372647), 7 / period));
        brush.GradientStops.Add(new GradientStop(Rgb(0x372647), 1));
        brush.Freeze();
        return new DrawingBrush
        {
            Drawing = new GeometryDrawing
            {
                Geometry = new RectangleGeometry(new Rect(0, 0, period, 1)),
                Brush = brush,
            },
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, period, 1),
            ViewportUnits = BrushMappingMode.Absolute,
        };
    }

    /// <summary>Soft coloured halo behind the art; a few effects tint it warmer/purpler.</summary>
    private static Brush ArtTint(EffectType t)
    {
        (int hex, byte alpha) = t switch
        {
            EffectType.Fire => (0x7B3124, (byte)0x1F),
            EffectType.AudioVU => (0x6641A9, (byte)0x25),
            EffectType.Custom => (0x8253B9, (byte)0x29),
            _ => (0x7041A7, (byte)0x25),
        };
        var c = Rgb(hex);
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.95,
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        b.Freeze();
        return b;
    }

    private FrameworkElement BuildEffectArt(EffectType t)
    {
        var canvas = new Grid { FlowDirection = FlowDirection.LeftToRight };  // art is directional
        var host = new Border
        {
            Height = ArtHeight,
            ClipToBounds = true,          // the tilted strips are meant to run off the tile edge
            Background = ArtTint(t),
            Child = canvas,
        };

        if (t == EffectType.Custom)
        {
            canvas.Children.Add(new TextBlock
            {
                Text = "\uE9E9",          // equaliser: Custom has no strip, it is a set of sliders
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 27,
                Foreground = Solid(0xB995E8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = Glow(0xC49BF6, 8, 0.27),
            });
            return host;
        }
        if (t == EffectType.AudioVU) { canvas.Children.Add(VuBars()); return host; }
        if (t == EffectType.Fire) { canvas.Children.Add(EmberBar()); return host; }
        if (t == EffectType.Comet) { canvas.Children.Add(CometStreak()); return host; }
        if (t == EffectType.Spectrum) { canvas.Children.Add(SpectrumBars()); return host; }
        if (t == EffectType.Scanner) { canvas.Children.Add(SegmentStrip(ScannerCells(), Rgb(0xAD80EE), 0.35)); return host; }
        if (t == EffectType.Sparkle) { canvas.Children.Add(SegmentStrip(SparkleCells(), Rgb(0xC9A9F5), 0.40)); return host; }
        if (t == EffectType.Ambient) { canvas.Children.Add(SegmentStrip(AmbientCells(), Rgb(0x9F7FE0), 0.35)); return host; }
        if (t == EffectType.Gaming) { canvas.Children.Add(SegmentStrip(GamingCells(), Rgb(0xE8E3F2), 0.30)); return host; }
        if (t == EffectType.Plasma)
        {
            // same palette as Rainbow, but smeared: plasma is a soft blend rather than discrete LEDs
            var spec = StripFor(EffectType.Rainbow);
            var soft = new Grid { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 5 } };
            soft.Children.Add(LedStrip(spec.Height, spec.Fill, spec.Glow, spec.GlowOpacity));
            canvas.Children.Add(soft);
            return host;
        }

        var spec2 = StripFor(t);
        canvas.Children.Add(LedStrip(spec2.Height, spec2.Fill, spec2.Glow, spec2.GlowOpacity));
        return host;
    }

    private static FrameworkElement LedStrip(double height, Brush fill, Color glow, double glowOpacity)
    {
        var group = new Grid
        {
            Height = height,
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform(-10),
        };

        var bar = new Border { Background = fill, CornerRadius = new CornerRadius(4) };
        bar.Effect = Glow(glow, 14, glowOpacity);
        group.Children.Add(bar);

        // 16 LED cells over the gradient — the separators are what make it read as hardware.
        var cells = new UniformGrid { Columns = 16 };
        var sep = new SolidColorBrush(Color.FromArgb(0x99, 0x16, 0x12, 0x1A));
        sep.Freeze();
        for (int i = 0; i < 16; i++)
            cells.Children.Add(new Border { BorderBrush = sep, BorderThickness = new Thickness(0, 0, 2, 0) });
        group.Children.Add(cells);

        // specular line just under the top edge
        group.Children.Add(new Rectangle
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Fill = Grad(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
        });
        return group;
    }

    /// <summary>
    /// A dark strip whose individual LEDs are lit from <paramref name="lit"/> (16 slots, null =
    /// unlit). Scanner, Sparkle, Ambient and Gaming all read as "some cells lit", which a plain
    /// gradient cannot express.
    /// </summary>
    private static FrameworkElement SegmentStrip(Color?[] lit, Color glow, double opacity)
    {
        var group = new Grid
        {
            Height = 6,
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform(-10),
        };

        var bar = new Border { Background = Solid(0x2A2333), CornerRadius = new CornerRadius(4) };
        bar.Effect = Glow(glow, 12, opacity);
        group.Children.Add(bar);

        var sep = new SolidColorBrush(Color.FromArgb(0x99, 0x16, 0x12, 0x1A));
        sep.Freeze();
        var cells = new UniformGrid { Columns = 16 };
        for (int i = 0; i < 16; i++)
        {
            var cell = new Border { BorderBrush = sep, BorderThickness = new Thickness(0, 0, 2, 0) };
            if (i < lit.Length && lit[i] is { } c)
                cell.Background = new SolidColorBrush(c);
            cells.Children.Add(cell);
        }
        group.Children.Add(cells);

        group.Children.Add(new Rectangle
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Fill = Grad(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
        });
        return group;
    }

    /// <summary>Scanner: a bright head sweeping across an otherwise dark strip.</summary>
    private static Color?[] ScannerCells()
    {
        var c = new Color?[16];
        c[4] = Color.FromArgb(0x55, 0xAD, 0x80, 0xEE);
        c[5] = Rgb(0xD9C2FF);
        c[6] = Color.FromArgb(0x66, 0xAD, 0x80, 0xEE);
        return c;
    }

    /// <summary>Sparkle: a few isolated bright cells, the rest dark.</summary>
    private static Color?[] SparkleCells()
    {
        var c = new Color?[16];
        c[2] = Rgb(0xEFE4FF);
        c[6] = Color.FromArgb(0x88, 0xC9, 0xA9, 0xF5);
        c[11] = Rgb(0xEFE4FF);
        c[14] = Color.FromArgb(0x66, 0xC9, 0xA9, 0xF5);
        return c;
    }

    /// <summary>Ambient mirrors the real effect: the screen's top / middle / bottom bands.</summary>
    private static Color?[] AmbientCells()
    {
        var c = new Color?[16];
        for (int i = 0; i < 16; i++)
            c[i] = i < 5 ? Rgb(0xE86674) : i < 11 ? Rgb(0x70D7BB) : Rgb(0x69BCF4);
        return c;
    }

    /// <summary>Gaming: dark body with the single white hit-flash the effect produces.</summary>
    private static Color?[] GamingCells()
    {
        var c = new Color?[16];
        c[9] = Rgb(0xFFFFFF);
        c[10] = Color.FromArgb(0x77, 0xFF, 0xFF, 0xFF);
        return c;
    }

    /// <summary>Spectrum: bars climbing left to right, the analyser silhouette.</summary>
    private static FrameworkElement SpectrumBars()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform(-10),
        };
        for (int i = 0; i < 9; i++)
        {
            row.Children.Add(new Border
            {
                Width = 6,
                Height = 6 + 24 * i / 8.0,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Grad(Rgb(0xC597FC), Rgb(0x825ABA)),
                Effect = Glow(0x9F71E5, 6, 0.25),
            });
        }
        return row;
    }

    private static FrameworkElement VuBars()
    {        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform(-7),
        };
        int[] heights = { 25, 44, 63, 37, 78, 54, 89, 62, 36, 65, 47, 24 };
        foreach (var h in heights)
        {
            var bar = new Border
            {
                Width = 4,
                Height = Math.Max(4, 31 * h / 100.0),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Grad(Rgb(0xC597FC), Rgb(0x825ABA)),
                Effect = Glow(0x9F71E5, 7, 0.30),
            };
            row.Children.Add(bar);
        }
        return row;
    }

    private static FrameworkElement EmberBar()
    {
        var bar = new Border
        {
            Height = 7,
            Margin = new Thickness(26, 0, 26, 0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
            Background = Grad(Rgb(0xBD2227), Rgb(0xF27E34), Rgb(0xF0B15B), Rgb(0xEF732D), Rgb(0xD93B25)),
            RenderTransform = new RotateTransform(-10),
            // the CSS throws a warm halo upward off the bar
            Effect = new DropShadowEffect
            {
                Color = Rgb(0xFA752A), BlurRadius = 15, ShadowDepth = 5, Direction = 270, Opacity = 0.45,
            },
        };
        return bar;
    }

    private static FrameworkElement CometStreak()
    {
        var group = new Grid
        {
            Height = 5,
            Margin = new Thickness(28, 0, 28, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new RotateTransform(-10),
        };
        var streak = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = Grad(
                Color.FromArgb(0x00, 0x5C, 0x54, 0x89),
                Color.FromArgb(0x22, 0x5C, 0x54, 0x89),
                Color.FromArgb(0x88, 0xA7, 0x84, 0xEF),
                Rgb(0xECDBFF)),
            Effect = Glow(0xB980F9, 6, 0.55),
        };
        group.Children.Add(streak);
        group.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = Glow(0xAD72FF, 9, 0.80),
        });
        return group;
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
        EffectType.Ambient => true,       // screen drives the colours; primary is the no-screen fallback
        EffectType.GamePulse => true,     // primary IS the danger colour; secondary is the healthy one
        _ => true,                        // Gaming included: primary paints when no screen sample exists
    };

    private void BuildParams()
    {
        EffectParams.Children.Clear();
        // Drop the reference: the old readout is detached now, and TickPreview must not keep
        // writing into a dead TextBlock when the selected effect is not the music one.
        _bandReadout = null;

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
            // No band selector any more. A fixed band only ever worked for some music - "bass" does
            // nothing on an acoustic track, "treble" nothing on hip-hop - so it had to be re-picked
            // per song. The provider now scores how rhythmic each band is and follows the winner
            // (AudioProvider.Analyse). This readout shows what it picked so the choice is visible
            // rather than a black box; it is refreshed live from TickPreview.
            _bandReadout = new TextBlock
            {
                Style = (Style)FindResource("FaintTxt"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AddRow(L10n.T("lbl.band"), _bandReadout);

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

        if (_edit.Type == EffectType.Gaming)
        {
            // Screen colour drives the strip; only the hit-flash strength is adjustable
            // (plus the primary colour shown as the no-screen fallback).
            AddSlider(L10n.T("lbl.beat"), _edit.BeatStrength, v => _edit.BeatStrength = v);
        }

        if (_edit.Type == EffectType.GamePulse)
        {
            // Health drives the body; the two colours are danger (primary) and healthy
            // (secondary), and the style picks solid vs. health-bar rendering.
            AddRow(L10n.T("lbl.color2"), ColorBox(_edit.Color2Hex, c => _edit.Color2Hex = c));
            var modes = new[] { "bar", "mirror", "dots" };
            var modeCmb = Combo(new[] { L10n.T("gp.solid"), L10n.T("gp.bar"), L10n.T("gp.dots") },
                            Math.Max(0, Array.IndexOf(modes, _edit.AudioMode)),
                            i => { _edit.AudioMode = modes[Math.Clamp(i, 0, 2)]; PushEdit(); });
            AddRow(L10n.T("gp.style"), modeCmb);
            EffectParams.Children.Add(new TextBlock
            {
                Text = L10n.T("fx.gamepulseHint"),
                Style = (Style)FindResource("FaintTxt"),
                Foreground = (Brush)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        if (_edit.Type == EffectType.Ambient)
        {
            // Brightness is the only meaningful knob: the colours come from the screen.
            // A short hint explains where the colours come from.
            EffectParams.Children.Add(new TextBlock
            {
                Text = L10n.T("fx.ambientHint"),
                Style = (Style)FindResource("FaintTxt"),
                Foreground = (Brush)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
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
    /// <summary>Applies the reduce-motion preference to a running preview, live.</summary>
    private void ApplyReduceMotion()
    {
        if (App.Settings.ReduceMotion)
        {
            _previewTimer?.Stop();
            _previewTimer = null;
            TickPreview();      // leave a current still frame, not a stale one
        }
        else StartPreview();
    }

    private void StartPreview()
    {
        if (_previewTimer is not null) return;
        _previewBmp = new WriteableBitmap(PreviewLeds, PreviewRows, 96, 96, PixelFormats.Bgr32, null);
        _previewPixels = new byte[PreviewLeds * PreviewRows * 4];
        PreviewImg.Source = _previewBmp;
        RenderOptions.SetBitmapScalingMode(PreviewImg, BitmapScalingMode.Linear);

        // Reduce-motion: draw ONE frame so the strip still shows what the effect looks like, then
        // leave it static. A 128-LED strip animating at 20 fps in the corner of the eye is exactly
        // the motion this setting exists to stop (and it costs battery on a laptop).
        if (App.Settings.ReduceMotion)
        {
            TickPreview();
            return;
        }

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

    /// <summary>Live readout of the band the auto detector is following (null when not built).</summary>
    private TextBlock? _bandReadout;

    private void TickPreview()
    {
        if (_previewBmp is null || _previewPixels is null) return;
        if (!IsVisible) return;   // hidden in tray: don't burn CPU

        // Live "which band is it following" readout, so the automatic choice is observable.
        if (_bandReadout is not null)
            _bandReadout.Text = _audio is null
                ? L10n.T("band.none")
                : L10n.T("band.auto", L10n.T("band." + Sensors.AudioProvider.BandName(_audio.AutoBandIndex)));

        var ctx = new EffectContext
        {
            Time = _previewClock.Elapsed.TotalSeconds,
            CpuTemp = _engine?.CpuTemp,
            GpuTemp = _engine?.GpuTemp,
            AudioLevel = _audio?.Level ?? 0,
            AudioBass = _audio?.Bass ?? 0,
            AudioMid = _audio?.Mid ?? 0,
            AudioTreble = _audio?.Treble ?? 0,
            AudioAuto = _audio?.AutoLevel ?? 0,
            Beat = _audio?.Beat ?? 0,
        };
        ctx.ScreenValid = _audio is not null && _audio.FillScreenContext(ctx);
        // the preview must show the same game events the hardware reacts to
        FullRGB.Sensors.GameEventState.Fill(ctx);
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
        SchedulePersistAfterEdit();
    }

    /// <summary>Debounced save after every editor change (round 19). Edits used to live ONLY in
    /// memory until Save was pressed or the app exited cleanly, so a crash/forced power-off lost
    /// the just-picked effect — "the last settings don't come up after boot". The debounce keeps
    /// slider drags from writing settings.json (and a backup) on every tick.</summary>
    private System.Windows.Threading.DispatcherTimer? _persistTimer;

    private void SchedulePersistAfterEdit()
    {
        if (_persistTimer is null)
        {
            _persistTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _persistTimer.Tick += (_, _) =>
            {
                _persistTimer!.Stop();
                try { ProfileStore.Save(App.Settings); } catch { /* disk full/locked: OnExit still saves */ }
            };
        }
        _persistTimer.Stop();
        _persistTimer.Start();
    }
}

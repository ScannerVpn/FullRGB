using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using Cursors = System.Windows.Input.Cursors;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace FullRGB;

/// <summary>
/// Themed HSV colour picker (saturation/value field + hue strip + hex box + presets).
/// Replaces System.Windows.Forms.ColorDialog, which is a grey Win32 dialog and looked
/// pasted in from another decade next to the dark UI.
/// </summary>
public sealed class ColorPickerDialog : Window
{
    private const int FieldW = 232, FieldH = 132, HueH = 14;

    private static readonly string[] Presets =
    {
        "#FF2D55", "#FF6B00", "#FFC24D", "#3DDC97", "#00E5FF",
        "#4D8DFF", "#7C4DFF", "#FF4D8D", "#FFFFFF", "#8593A4",
    };

    private readonly WriteableBitmap _field = new(FieldW, FieldH, 96, 96, PixelFormats.Bgr32, null);
    private readonly WriteableBitmap _hue = new(256, 1, 96, 96, PixelFormats.Bgr32, null);
    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Canvas _fieldCanvas;
    private readonly Ellipse _cursor;
    private readonly Border _hueMarker;
    private readonly List<Button> _presets = new();

    private double _h, _s = 1, _v = 1;
    private bool _syncing;

    public Color Selected { get; private set; }

    public ColorPickerDialog(Window? owner, Color initial)
    {
        Owner = owner;
        Width = 288;
        SizeToContent = SizeToContent.Height;   // hug the content: no dead space under the buttons
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        FlowDirection = FlowDirection.LeftToRight;  // colour geometry is not mirrored
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        Selected = initial;
        (_h, _s, _v) = ToHsv(initial);

        // ---- SV field ----
        var img = new Image { Source = _field, Width = FieldW, Height = FieldH, Stretch = Stretch.Fill };
        _cursor = new Ellipse
        {
            Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2,
            Fill = Brushes.Transparent, IsHitTestVisible = false,
            // dark halo so the ring stays visible over the black bottom edge too
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 5, ShadowDepth = 0, Opacity = 1.0, Color = Colors.Black },
        };
        _fieldCanvas = new Canvas { Width = FieldW, Height = FieldH, Background = Brushes.Transparent };
        _fieldCanvas.Children.Add(img);
        _fieldCanvas.Children.Add(_cursor);
        // The cursor must stay visible at the corners, so the canvas is NOT clipped and the
        // frame around it is sized to leave room (previously S=V=1 put it outside the clip
        // and the selection had no marker at all).
        _fieldCanvas.ClipToBounds = false;
        _fieldCanvas.MouseLeftButtonDown += (_, e) => { _fieldCanvas.CaptureMouse(); PickField(e.GetPosition(_fieldCanvas)); };
        _fieldCanvas.MouseMove += (_, e) => { if (_fieldCanvas.IsMouseCaptured) PickField(e.GetPosition(_fieldCanvas)); };
        _fieldCanvas.MouseLeftButtonUp += (_, _) => _fieldCanvas.ReleaseMouseCapture();

        var fieldFrame = new Border
        {
            CornerRadius = new CornerRadius(10),
            ClipToBounds = false,
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            Child = _fieldCanvas,
            Margin = new Thickness(0, 0, 0, 12),
        };

        // ---- hue strip ----
        var hueImg = new Image { Source = _hue, Height = HueH, Stretch = Stretch.Fill };
        _hueMarker = new Border
        {
            Width = 3, Height = HueH, Background = Brushes.White, CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left,
        };
        var hueGrid = new Grid { Height = HueH, Width = FieldW };
        hueGrid.Children.Add(hueImg);
        hueGrid.Children.Add(_hueMarker);
        var hueHost = new Border
        {
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Child = hueGrid,
            Margin = new Thickness(0, 0, 0, 12),
            Cursor = Cursors.Hand,
        };
        hueHost.MouseLeftButtonDown += (_, e) => { hueHost.CaptureMouse(); PickHue(e.GetPosition(hueGrid).X); };
        hueHost.MouseMove += (_, e) => { if (hueHost.IsMouseCaptured) PickHue(e.GetPosition(hueGrid).X); };
        hueHost.MouseLeftButtonUp += (_, _) => hueHost.ReleaseMouseCapture();

        // ---- hex + swatch ----
        _swatch = new Border
        {
            Width = 40, Height = 28, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(initial),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 9, 0),
        };
        _hex = new TextBox
        {
            Style = (Style)Application.Current.FindResource("Inp"),
            Text = ToHex(initial),
            MaxLength = 7,
            // stretches to the field's right edge so the row lines up with everything else
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _hex.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var c = Theme.Parse(_hex.Text, Selected);
            Selected = c;
            (_h, _s, _v) = ToHsv(c);
            Redraw(updateHex: false);
        };

        var hexRow = new Grid { Margin = new Thickness(0, 0, 0, 12), Width = FieldW };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hexRow.Children.Add(_swatch);
        Grid.SetColumn(_hex, 1);
        hexRow.Children.Add(_hex);

        // ---- presets ----
        var presetPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 14), Width = FieldW };
        foreach (var p in Presets)
        {
            var c = Theme.Parse(p, Colors.White);
            var b = new Button
            {
                Width = 25, Height = 25, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
                Template = SwatchTemplate(c),
                ToolTip = p,
                Tag = c,
            };
            b.Click += (_, _) => { Selected = c; (_h, _s, _v) = ToHsv(c); Redraw(); };
            presetPanel.Children.Add(b);
            _presets.Add(b);
        }

        // ---- buttons ----
        var ok = new Button
        {
            Style = (Style)Application.Current.FindResource("AccentBtn"),
            Content = L10n.T("dlg.ok"), IsDefault = true,
        };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostBtn"),
            Content = L10n.T("dlg.cancel"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0),
        };
        cancel.Click += (_, _) => DialogResult = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel { Width = FieldW };
        body.Children.Add(new TextBlock
        {
            Text = L10n.T("dlg.pickColor"),
            Style = (Style)Application.Current.FindResource("Txt"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        });
        body.Children.Add(fieldFrame);
        body.Children.Add(hueHost);
        body.Children.Add(hexRow);
        body.Children.Add(presetPanel);
        body.Children.Add(buttons);

        Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("BgElevated"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
            Child = body,
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        DrawHue();
        Redraw();
    }

    private static ControlTemplate SwatchTemplate(Color c)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(c));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void PickField(Point p)
    {
        _s = Math.Clamp(p.X / FieldW, 0, 1);
        _v = Math.Clamp(1 - p.Y / FieldH, 0, 1);
        Commit();
    }

    private void PickHue(double x)
    {
        _h = Math.Clamp(x / FieldW, 0, 1);
        DrawField();
        Commit();
    }

    private void Commit()
    {
        var (r, g, b) = Effects.EffectRenderer.Hsv(_h, _s, _v);
        Selected = Color.FromRgb(r, g, b);
        Redraw();
    }

    private void Redraw(bool updateHex = true)
    {
        DrawField();
        _swatch.Background = new SolidColorBrush(Selected);
        // keep the ring fully inside the field so it stays visible at the corners
        double cx = Math.Clamp(_s * FieldW, 7, FieldW - 7);
        double cy = Math.Clamp((1 - _v) * FieldH, 7, FieldH - 7);
        Canvas.SetLeft(_cursor, cx - 6);
        Canvas.SetTop(_cursor, cy - 6);
        _cursor.Stroke = Theme.Luminance(Selected) > 0.6 ? Brushes.Black : Brushes.White;
        _hueMarker.Margin = new Thickness(Math.Clamp(_h * FieldW - 1.5, 0, FieldW - 3), 0, 0, 0);
        // mark the preset that matches the current colour
        foreach (var b in _presets)
            b.Opacity = b.Tag is Color pc && pc == Selected ? 1.0 : 0.72;
        if (updateHex)
        {
            _syncing = true;
            _hex.Text = ToHex(Selected);
            _syncing = false;
        }
    }

    private void DrawField()
    {
        var px = new byte[FieldW * FieldH * 4];
        for (int y = 0; y < FieldH; y++)
        {
            double v = 1 - (double)y / (FieldH - 1);
            for (int x = 0; x < FieldW; x++)
            {
                double s = (double)x / (FieldW - 1);
                var (r, g, b) = Effects.EffectRenderer.Hsv(_h, s, v);
                int o = (y * FieldW + x) * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
            }
        }
        _field.WritePixels(new Int32Rect(0, 0, FieldW, FieldH), px, FieldW * 4, 0);
    }

    private void DrawHue()
    {
        // 256×1 bitmap stretched to the strip width: cheap and crisp at any width
        var px = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = Effects.EffectRenderer.Hsv(i / 255.0, 1, 1);
            px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255;
        }
        _hue.WritePixels(new Int32Rect(0, 0, 256, 1), px, 256 * 4, 0);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    /// <summary>Shows the picker; returns the new hex string or null when cancelled.</summary>
    public static string? Pick(Window? owner, string currentHex)
    {
        var dlg = new ColorPickerDialog(owner, Theme.Parse(currentHex, Color.FromRgb(0, 229, 255)));
        return dlg.ShowDialog() == true ? ToHex(dlg.Selected) : null;
    }
}

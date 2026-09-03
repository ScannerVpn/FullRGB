using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FullRGB.Config;
using FullRGB.Effects;
using FullRGB.SDK;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace FullRGB;

/// <summary>Device list, zone rows and per-device colour correction.</summary>
public partial class MainWindow
{
    // ---------- lighting page: target selector ----------

    private void BuildDeviceList()
    {
        DeviceList.Children.Clear();
        if (_client is null) return;
        var profile = CurrentProfile();

        var allRow = new RadioButton
        {
            GroupName = "devsel",
            Style = (Style)FindResource("DeviceRow"),
            Content = Row("\uE80F", L10n.T("hero.global"), L10n.T("dev.allHint")),
            IsChecked = _target == TargetMode.Global,
        };
        allRow.Checked += (_, _) => SelectTarget(TargetMode.Global, null, -1);
        DeviceList.Children.Add(allRow);

        foreach (var dev in _client.Controllers)
        {
            bool excluded = profile.IsExcluded(dev);
            bool hasOverride = profile.DeviceOverrides.ContainsKey(dev.Key);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chip = new RadioButton
            {
                GroupName = "devsel",
                Style = (Style)FindResource("DeviceRow"),
                Tag = dev.Key,
                Content = BuildDeviceChip(dev, excluded, hasOverride),
                IsChecked = _target == TargetMode.Device && _selectedKey == dev.Key,
            };
            chip.Checked += (_, _) => SelectTarget(TargetMode.Device, dev.Key, -1);

            var menuBtn = new Button
            {
                Style = (Style)FindResource("IconBtn"),
                Content = "\uE712",
                Tag = dev.Key,
                Margin = new Thickness(7, 0, 0, 7),
                VerticalAlignment = VerticalAlignment.Center,
            };
            menuBtn.Click += DeviceMenu_Click;
            Grid.SetColumn(menuBtn, 1);
            grid.Children.Add(chip);
            grid.Children.Add(menuBtn);
            DeviceList.Children.Add(grid);

            // Zone rows appear nested under the selected device: this is how a zone gets
            // its own effect without leaving the Lighting page.
            if (_selectedKey == dev.Key && _target is TargetMode.Device or TargetMode.Zone && !excluded)
                AddZoneTargets(dev, profile);
        }

        TargetHdr.Text = L10n.T("section.devices", _client.Controllers.Count);
        OverrideHint.Text = L10n.T("msg.overrideHint");
    }

    private void AddZoneTargets(RgbController dev, Profile profile)
    {
        foreach (var zone in dev.Zones.Where(z => z.LedsCount > 0))
        {
            bool has = profile.HasZoneOverride(dev, zone);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(2, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource(has ? "Accent" : "Faint"),
            };
            content.Children.Add(dot);

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(line, 1);
            line.Children.Add(new TextBlock
            {
                Text = zone.Name,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Text"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            line.Children.Add(new TextBlock
            {
                Text = "  " + L10n.T("dev.leds", zone.LedsCount),
                Style = (Style)FindResource("FaintTxt"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (has) line.Children.Add(Badge(L10n.T("zone.overrideOn"), "Accent", "AccentDim"));
            content.Children.Add(line);

            var rb = new RadioButton
            {
                GroupName = "devsel",
                Style = (Style)FindResource("DeviceRow"),
                Content = content,
                Margin = new Thickness(20, 0, 0, 7),
                IsChecked = _target == TargetMode.Zone && _selectedKey == dev.Key && _selectedZone == zone.Index,
            };
            int zi = zone.Index;
            rb.Checked += (_, _) => SelectTarget(TargetMode.Zone, dev.Key, zi);
            DeviceList.Children.Add(rb);
        }
    }

    /// <summary>Points the editor at the global effect, a device or a single zone.</summary>
    private void SelectTarget(TargetMode mode, string? key, int zoneIndex)
    {
        if (_loadingUi) return;
        _target = mode;
        _selectedKey = key;
        _selectedZone = zoneIndex;
        _edit = CurrentEditTarget();
        BuildDeviceList();
        BuildEffectEditor();
    }

    /// <summary>The effect the editor should show for the current target.</summary>
    private EffectDef CurrentEditTarget()
    {
        var profile = CurrentProfile();
        var dev = _client?.Controllers.FirstOrDefault(c => c.Key == _selectedKey);
        if (_target == TargetMode.Zone && dev is not null)
        {
            var zone = dev.Zones.FirstOrDefault(z => z.Index == _selectedZone);
            if (zone is not null) return EffectEngine.Clone(profile.EffectFor(dev, zone));
        }
        if (_target == TargetMode.Device && dev is not null)
            return EffectEngine.Clone(profile.EffectFor(dev));
        return EffectEngine.Clone(profile.GlobalEffect);
    }

    /// <summary>Icon tile + two text lines, the shared device/summary row layout.</summary>
    private UIElement Row(string glyph, string title, string subtitle)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tile = IconTile(glyph);
        g.Children.Add(tile);

        var col = new StackPanel();
        Grid.SetColumn(col, 1);
        col.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        col.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Faint"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        g.Children.Add(col);
        return g;
    }

    private Border IconTile(string glyph) => new()
    {
        Width = 32, Height = 32,
        CornerRadius = new CornerRadius(9),
        Background = (Brush)FindResource("Bg"),
        BorderBrush = (Brush)FindResource("Border"),
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 11, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Two-line device row: icon + name on top, kind · LEDs · badges underneath.</summary>
    private UIElement BuildDeviceChip(RgbController dev, bool excluded, bool hasOverride)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(IconTile(DeviceGlyph(dev.Kind)));

        var col = new StackPanel();
        Grid.SetColumn(col, 1);
        col.Children.Add(new TextBlock
        {
            Text = ShortName(dev.Name),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        meta.Children.Add(new TextBlock
        {
            Text = $"{KindLabel(dev.Kind)} · {L10n.T("dev.leds", dev.LedCount)} · " +
                   L10n.T("zone.title", dev.Zones.Count(z => z.LedsCount > 0)),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Faint"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (dev.ParseFailed) meta.Children.Add(Badge(L10n.T("device.parseFailed"), "Danger", "DangerBg"));
        else if (excluded) meta.Children.Add(Badge(L10n.T("device.excluded"), "Danger", "DangerBg"));
        else if (hasOverride) meta.Children.Add(Badge(L10n.T("device.override"), "Warn", "WarnBg"));
        col.Children.Add(meta);

        g.Children.Add(col);
        return g;
    }

    /// <summary>Small rounded pill used for per-row state.</summary>
    private UIElement Badge(string text, string fgKey, string bgKey) => new Border
    {
        Background = (Brush)FindResource(bgKey),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(7, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(fgKey),
        },
    };

    private static string KindLabel(RgbDeviceType kind) => kind switch
    {
        RgbDeviceType.Motherboard => L10n.T("kind.motherboard"),
        RgbDeviceType.DRAM => L10n.T("kind.dram"),
        RgbDeviceType.GPU => L10n.T("kind.gpu"),
        RgbDeviceType.Cooler => L10n.T("kind.cooler"),
        RgbDeviceType.LedStrip => L10n.T("kind.ledstrip"),
        RgbDeviceType.Case => L10n.T("kind.case"),
        RgbDeviceType.Light => L10n.T("kind.light"),
        RgbDeviceType.Storage => L10n.T("kind.storage"),
        _ => L10n.T("kind.other"),
    };

    /// <summary>Segoe MDL2 glyphs, mapped from the REAL OpenRGB device_type enum.</summary>
    private static string DeviceGlyph(RgbDeviceType kind) => kind switch
    {
        RgbDeviceType.Motherboard => "\uE964",
        RgbDeviceType.DRAM => "\uE950",
        RgbDeviceType.GPU => "\uE7F4",
        RgbDeviceType.Cooler => "\uEA80",
        RgbDeviceType.LedStrip => "\uE781",
        RgbDeviceType.Keyboard => "\uE92E",
        RgbDeviceType.Mouse => "\uE962",
        RgbDeviceType.Case => "\uE7F8",
        RgbDeviceType.Storage => "\uEDA2",
        RgbDeviceType.Light => "\uE781",
        _ => "\uE770",
    };

    private static string ShortName(string name)
    {
        name = name.Replace("ASUS ROG ", "").Replace("Corsair ", "").Replace("ASUS ", "");
        return name.Length > 34 ? name[..34] + "…" : name;
    }

    private void DeviceMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        var dev = _client?.Controllers.FirstOrDefault(c => c.Key == key);
        if (dev is null) return;
        var profile = CurrentProfile();

        var menu = new System.Windows.Controls.ContextMenu();
        if (profile.IsExcluded(dev))
        {
            var mi = new System.Windows.Controls.MenuItem { Header = L10n.T("device.backToGlobal") };
            mi.Click += (_, _) =>
            {
                profile.ExcludedDevices.RemoveAll(x => x == dev.Key || x.Equals(dev.Name, StringComparison.OrdinalIgnoreCase));
                SaveAndRebuild();
            };
            menu.Items.Add(mi);
        }
        else
        {
            var mi = new System.Windows.Controls.MenuItem { Header = L10n.T("device.exclude") };
            mi.Click += (_, _) => { profile.ExcludedDevices.Add(dev.Key); SaveAndRebuild(); };
            menu.Items.Add(mi);
        }
        if (profile.DeviceOverrides.ContainsKey(dev.Key))
        {
            var mi2 = new System.Windows.Controls.MenuItem { Header = L10n.T("device.clearOverride") };
            mi2.Click += (_, _) => { profile.DeviceOverrides.Remove(dev.Key); SaveAndRebuild(); };
            menu.Items.Add(mi2);
        }
        // Zone overrides are only reachable from here once they exist, so offer a bulk clear.
        if (dev.Zones.Any(z => profile.HasZoneOverride(dev, z)))
        {
            var mi3 = new System.Windows.Controls.MenuItem { Header = L10n.T("zone.clearOverride") };
            mi3.Click += (_, _) =>
            {
                foreach (var z in dev.Zones) profile.ZoneOverrides.Remove(Profile.ZoneKey(dev, z));
                SaveAndRebuild();
            };
            menu.Items.Add(mi3);
        }
        menu.PlacementTarget = b;
        menu.IsOpen = true;
    }

    private void SaveAndRebuild()
    {
        ProfileStore.Save(App.Settings);
        _engine?.Apply(CurrentProfile());
        _edit = CurrentEditTarget();
        BuildDeviceList();
        BuildEffectEditor();
    }
}

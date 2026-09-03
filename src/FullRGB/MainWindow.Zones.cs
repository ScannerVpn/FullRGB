using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullRGB.Config;
using FullRGB.SDK;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Slider = System.Windows.Controls.Slider;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace FullRGB;

/// <summary>Devices tab: pick a device, set real zone LED counts, trim its colour.</summary>
public partial class MainWindow
{
    private readonly Dictionary<string, TextBox> _zoneInputs = new();

    /// <summary>
    /// The Devices tab used to depend on a selection made on the Lighting tab, which left it
    /// empty and confusing. It now has its own device picker.
    /// </summary>
    private void BuildDevicePicker()
    {
        DevPickList.Children.Clear();
        bool any = _client is { Controllers.Count: > 0 };
        // Empty state instead of a header card with no rows under it.
        DevEmptyCard.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        DevPickCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        DevEmptyTxt.Text = L10n.T("dev.none");
        DevEmptyBtn.Content = L10n.T("btn.rescan");
        HelpExp.IsExpanded = !any;   // the troubleshooting text is the point when nothing is found
        if (!any)
        {
            ZoneCard.Visibility = Visibility.Collapsed;
            CalCard.Visibility = Visibility.Collapsed;
            return;
        }
        if (_devKey is null || _client!.Controllers.All(c => c.Key != _devKey))
            _devKey = _client!.Controllers.FirstOrDefault()?.Key;

        foreach (var dev in _client!.Controllers)
        {
            var rb = new RadioButton
            {
                GroupName = "devtab",
                Style = (Style)FindResource("DeviceRow"),
                Content = BuildDeviceChip(dev, CurrentProfile().IsExcluded(dev), false),
                IsChecked = dev.Key == _devKey,
                Tag = dev.Key,
            };
            rb.Checked += (_, _) =>
            {
                _devKey = dev.Key;
                BuildZoneList();
            };
            DevPickList.Children.Add(rb);
        }
        BuildZoneList();
    }

    private RgbController? DevTabDevice() => _client?.Controllers.FirstOrDefault(c => c.Key == _devKey);

    private void BuildZoneList()
    {
        ZoneList.Children.Clear();
        _zoneInputs.Clear();
        var dev = DevTabDevice();
        if (dev is null)
        {
            ZoneCard.Visibility = Visibility.Collapsed;
            CalCard.Visibility = Visibility.Collapsed;
            return;
        }
        ZoneCard.Visibility = Visibility.Visible;
        CalCard.Visibility = Visibility.Visible;
        ZoneHdr.Text = L10n.T("zone.title", dev.Zones.Count);
        ZoneHint.Text = L10n.T("zone.hint");
        ZoneApplyBtn.Content = L10n.T("zone.apply");

        var profile = CurrentProfile();
        foreach (var zone in dev.Zones)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 7, Height = 7,
                Fill = (Brush)FindResource(zone.LedsCount > 0 ? "Accent" : "Faint"),
                Margin = new Thickness(2, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            g.Children.Add(dot);

            var nameTxt = new TextBlock
            {
                Text = zone.Name,
                Style = (Style)FindResource("Txt"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(nameTxt, 1);
            g.Children.Add(nameTxt);

            // A fixed zone gets a visible reason, so its greyed-out box does not look broken.
            if (!zone.IsResizable)
            {
                var lockTxt = new TextBlock
                {
                    Text = L10n.T("zone.fixedTag"),
                    Style = (Style)FindResource("FaintTxt"),
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 1, 12, 0),
                };
                Grid.SetColumn(lockTxt, 1);
                lockTxt.HorizontalAlignment = HorizontalAlignment.Right;
                g.Children.Add(lockTxt);
            }

            if (zone.IsResizable)
            {
                var tb = new TextBox
                {
                    Style = (Style)FindResource("Inp"),
                    Text = profile.ZoneSize(dev, zone).ToString(),
                    MaxLength = 4,
                    FontSize = 11.5,
                    Padding = new Thickness(4),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"{zone.LedsMin}–{zone.LedsMax}",
                };
                // digits only: a stray letter used to silently reset the zone to max
                tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
                Grid.SetColumn(tb, 2);
                g.Children.Add(tb);
                _zoneInputs[Profile.ZoneKey(dev, zone)] = tb;
            }
            else
            {
                // A fixed zone still renders as an input BOX, just disabled: a bare TextBlock made
                // the row shorter than the resizable ones and broke both the numeric column and
                // the vertical rhythm of the list.
                var tb = new TextBox
                {
                    Style = (Style)FindResource("Inp"),
                    Text = zone.LedsCount.ToString(),
                    FontSize = 11.5,
                    Padding = new Thickness(4),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = false,
                    ToolTip = L10n.T("zone.fixed"),
                };
                Grid.SetColumn(tb, 2);
                g.Children.Add(tb);
            }

            var idBtn = new Button
            {
                Style = (Style)FindResource("IconBtn"),
                Content = "\uE781",
                ToolTip = L10n.T("zone.identify"),
                Margin = new Thickness(7, 0, 0, 0),
                Width = 29, Height = 27,
                Tag = (dev.Index, zone.Index, (int)zone.LedsCount),
                IsEnabled = zone.LedsCount > 0,
            };
            System.Windows.Automation.AutomationProperties.SetName(idBtn, L10n.T("zone.identify"));
            idBtn.Click += Identify_Click;
            Grid.SetColumn(idBtn, 3);
            g.Children.Add(idBtn);

            ZoneList.Children.Add(g);
        }

        BuildCalibration(dev);
    }

    // ---------- per-device colour correction ----------

    /// <summary>Selected calibration scope on the Devices page (-1 = whole device, else zone index).</summary>
    private int _calZone = -1;

    /// <summary>
    /// Trim sliders for R/G/B gain + gamma. Scope picker on top: whole device, or one zone
    /// (the pump ring and the fans are different chips on the same Commander Core, so a
    /// single device-wide trim cannot fix both).
    /// </summary>
    private void BuildCalibration(RgbController dev)
    {
        CalPanel.Children.Clear();
        CalHdr.Text = L10n.T("cal.title");
        CalHint.Text = L10n.T("cal.hint");

        var profile = CurrentProfile();
        var scope = new ComboBox { Style = (Style)FindResource("Cmb"), Margin = new Thickness(0, 0, 0, 10) };
        scope.Items.Add(L10n.T("cal.whole"));
        foreach (var z in dev.Zones) scope.Items.Add(z.Name);
        scope.SelectedIndex = _calZone < 0 ? 0
            : dev.Zones.FindIndex(z => z.Index == _calZone) + 1;
        if (scope.SelectedIndex < 0 || scope.SelectedIndex >= scope.Items.Count) scope.SelectedIndex = 0;
        scope.SelectionChanged += (_, _) =>
        {
            _calZone = scope.SelectedIndex <= 0 ? -1
                : dev.Zones[Math.Clamp(scope.SelectedIndex - 1, 0, dev.Zones.Count - 1)].Index;
            BuildCalibration(dev);
        };
        CalPanel.Children.Add(scope);

        RgbZone? scopeZone = _calZone < 0 ? null : dev.Zones.FirstOrDefault(z => z.Index == _calZone);
        string scopeKey = scopeZone is null ? dev.Key : Profile.ZoneKey(dev, scopeZone);
        var cal = (scopeZone is null ? profile.CalibrationFor(dev) : profile.CalibrationFor(dev, scopeZone)).Clone();

        void Persist()
        {
            if (scopeZone is null) profile.Calibrations[dev.Key] = cal.Clone();
            else profile.ZoneCalibrations[scopeKey] = cal.Clone();
            _engine?.Apply(profile);
        }

        void Row(string label, double value, double min, double max, Action<double> set)
        {
            var sl = new Slider
            {
                Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
                Style = (Style)FindResource("Slider"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var readout = new TextBlock
            {
                Style = (Style)FindResource("FaintTxt"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 34,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(10, 0, 0, 0),
                Text = sl.Value.ToString("0.00"),
            };
            sl.ValueChanged += (_, _) =>
            {
                set(sl.Value);
                readout.Text = sl.Value.ToString("0.00");
                Persist();
            };

            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("MutedTxt"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(sl, 1);
            g.Children.Add(sl);
            Grid.SetColumn(readout, 2);
            g.Children.Add(readout);
            CalPanel.Children.Add(g);
        }

        Row(L10n.T("cal.red"), cal.RGain, 0, 1.5, v => cal.RGain = v);
        Row(L10n.T("cal.green"), cal.GGain, 0, 1.5, v => cal.GGain = v);
        Row(L10n.T("cal.blue"), cal.BGain, 0, 1.5, v => cal.BGain = v);
        Row(L10n.T("cal.gamma"), cal.Gamma, 0.5, 2.5, v => cal.Gamma = v);

        var reset = new Button
        {
            Style = (Style)FindResource("GhostBtn"),
            Content = L10n.T("cal.reset"),
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 11,
        };
        reset.Click += (_, _) =>
        {
            if (scopeZone is null) profile.Calibrations.Remove(dev.Key);
            else profile.ZoneCalibrations.Remove(scopeKey);
            ProfileStore.Save(App.Settings);
            _engine?.Apply(profile);
            BuildCalibration(dev);
        };
        CalPanel.Children.Add(reset);
    }

    /// <summary>Flashes one zone white so the user can see which physical port it is.</summary>
    private async void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ValueTuple<int, int, int> tag || _client is null) return;
        var (devIdx, zoneIdx, leds) = tag;
        if (leds <= 0) return;
        b.IsEnabled = false;
        bool wasRunning = _engine?.IsRunning == true;
        _engine?.Stop();
        try
        {
            var white = new byte[leds * 3];
            Array.Fill(white, (byte)255);
            var off = new byte[leds * 3];
            for (int k = 0; k < 6; k++)
            {
                _client.UpdateZoneLeds(devIdx, zoneIdx, k % 2 == 0 ? white : off);
                await Task.Delay(220);
            }
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
        finally
        {
            b.IsEnabled = true;
            if (wasRunning) _engine?.Apply(CurrentProfile());
            SyncRunButtons();
        }
    }

    private async void ZoneApply_Click(object sender, RoutedEventArgs e)
    {
        var dev = DevTabDevice();
        if (dev is null || _client is null) return;
        var profile = CurrentProfile();
        foreach (var (key, tb) in _zoneInputs)
        {
            var zone = dev.Zones.FirstOrDefault(z => Profile.ZoneKey(dev, z) == key);
            if (zone is null) continue;
            if (int.TryParse(tb.Text.Trim(), out int n) && n > 0)
                profile.ZoneSizes[key] = (int)Math.Clamp((uint)n, zone.LedsMin, zone.LedsMax);
            else profile.ZoneSizes.Remove(key);
        }
        ProfileStore.Save(App.Settings);

        ZoneApplyBtn.IsEnabled = false;
        SetStatus(L10n.T("scan.step.zones"), StatusKind.Busy);
        bool wasRunning = _engine?.IsRunning == true;
        _engine?.Stop();
        try
        {
            foreach (var zone in dev.Zones.Where(z => z.IsResizable))
                if (_zoneInputs.TryGetValue(Profile.ZoneKey(dev, zone), out var input))
                    input.Text = profile.ZoneSize(dev, zone).ToString();

            await Task.Run(() =>
            {
                foreach (var zone in dev.Zones.Where(z => z.IsResizable))
                {
                    int want = profile.ZoneSize(dev, zone);
                    if (want != zone.LedsCount)
                    {
                        _client.ResizeZone(dev.Index, zone.Index, want);
                        Thread.Sleep(120);
                    }
                }
                Thread.Sleep(500);
                _client.RefreshControllers();
                _client.EnsureDirectMode();
            });
            BuildDeviceList();
            BuildDevicePicker();
            RefreshStatus();
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
        finally
        {
            ZoneApplyBtn.IsEnabled = true;
            if (wasRunning) _engine?.Apply(CurrentProfile());
            SyncRunButtons();
        }
    }
}

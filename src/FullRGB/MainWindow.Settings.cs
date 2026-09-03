using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullRGB.Config;
using FullRGB.Effects;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace FullRGB;

/// <summary>Action bar, profiles, appearance and startup settings.</summary>
public partial class MainWindow
{
    // ---------- action bar ----------

    /// <summary>Keeps the Start/Stop label honest — it used to drift out of sync with the engine.</summary>
    private void SyncRunButtons()
    {
        bool running = _engine?.IsRunning == true;
        StopBtn.Content = running ? L10n.T("btn.stopEffects") : L10n.T("btn.startEffects");
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        PushEdit();
        ProfileStore.Save(App.Settings);
        if (_client is not null)
            foreach (var dev in _client.Controllers.Where(c => c.LedCount > 0))
                try { _client.SaveMode(dev.Index); } catch { }
        SetStatus(L10n.T("msg.saved"), StatusKind.Ok);
    }

    private void StopEffects_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (_engine.IsRunning)
        {
            _engine.Stop();
            SetStatus(L10n.T("status.stopped"), StatusKind.Info);
        }
        else
        {
            _engine.Apply(CurrentProfile());
            RefreshStatus();
        }
        SyncRunButtons();
    }

    private void Blackout_Click(object sender, RoutedEventArgs e)
    {
        _engine?.Stop();
        _engine?.Blackout();
        SetStatus(L10n.T("status.stopped"), StatusKind.Info);
        SyncRunButtons();
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = RescanAsync();

    private async Task RescanAsync()
    {
        if (_client is null) { await ConnectAsync(); return; }
        StatusPill.IsEnabled = false;
        bool wasRunning = _engine?.IsRunning == true;
        _engine?.Stop();
        try
        {
            SetStatus(L10n.T("scan.step.detect"), StatusKind.Busy);
            var profile = CurrentProfile();
            await Task.Run(() =>
            {
                _client!.Reconnect();
                _client.ExpandAllZones(default, (d, z) => profile.ZoneSize(d, z));
            });
            profile.PruneTo(_client.Controllers);
            BuildDeviceList();
            BuildDevicePicker();
            RefreshStatus();
            CheckSmbus();
            if (wasRunning || App.Settings.AutoStartEffects) _engine?.Apply(profile);
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
        finally
        {
            StatusPill.IsEnabled = true;
            SyncRunButtons();
        }
    }

    // ---------- profiles ----------

    private void LoadProfileToUi()
    {
        _loadingUi = true;
        ProfileCmb.ItemsSource = App.Settings.Profiles.Select(p => p.Name).ToList();
        ProfileCmb.SelectedItem = CurrentProfile().Name;
        AutostartChk.IsChecked = App.Settings.StartWithWindows;
        MinimizedChk.IsChecked = App.Settings.StartMinimized;
        AutoFxChk.IsChecked = App.Settings.AutoStartEffects;
        CloseEngineChk.IsChecked = App.Settings.CloseEngineOnExit;
        BuildAccentSwatches();
        RefreshAdvanced();
        _loadingUi = false;
    }

    private void ProfileCmb_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi) return;
        if (ProfileCmb.SelectedItem is not string name) return;
        SwitchProfile(name);
    }

    /// <summary>Single path for changing profile, shared by the ComboBox and the tray menu.</summary>
    private void SwitchProfile(string name)
    {
        if (!App.Settings.Profiles.Any(p => p.Name == name)) return;
        App.Settings.ActiveProfile = name;
        _target = TargetMode.Global;
        _selectedKey = null;
        _selectedZone = -1;
        _edit = EffectEngine.Clone(CurrentProfile().GlobalEffect);
        _engine?.Apply(CurrentProfile());
        LoadProfileToUi();
        BuildDeviceList();
        BuildEffectEditor();
        ProfileStore.Save(App.Settings);
        SyncRunButtons();
    }

    private void ProfileNew_Click(object sender, RoutedEventArgs e)
    {
        var suggested = "Profile " + (App.Settings.Profiles.Count + 1);
        var name = PromptDialog.Ask(this, L10n.T("dlg.newProfile"), suggested);
        if (name is null) return;
        // Copy the current profile: starting from a blank effect is almost never what users want.
        var clone = EffectEngine.Clone(CurrentProfile());
        clone.Name = name;
        App.Settings.Profiles.Add(clone);
        App.Settings.Normalized();
        SwitchProfile(App.Settings.Profiles[^1].Name);
    }

    private void ProfileRename_Click(object sender, RoutedEventArgs e)
    {
        var profile = CurrentProfile();
        var name = PromptDialog.Ask(this, L10n.T("dlg.rename"), profile.Name);
        if (name is null || name == profile.Name) return;
        profile.Name = name;
        App.Settings.ActiveProfile = name;
        App.Settings.Normalized();
        ProfileStore.Save(App.Settings);
        LoadProfileToUi();
    }

    private void ProfileDel_Click(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Profiles.Count <= 1) return;
        var profile = CurrentProfile();
        if (!ConfirmDialog.Ask(this, L10n.T("dlg.delProfile", profile.Name), L10n.T("dlg.delete"), danger: true))
            return;
        App.Settings.Profiles.Remove(profile);
        SwitchProfile(App.Settings.Profiles[0].Name);
    }

    // ---------- appearance ----------

    private void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var (name, hex) in Theme.Accents)
        {
            bool active = string.Equals(hex, App.Settings.AccentHex, StringComparison.OrdinalIgnoreCase);
            var color = Theme.Parse(hex, Colors.White);
            var dot = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(color),
                // a check INSIDE the swatch, tinted for contrast, works on white too —
                // a white outer ring was invisible on the white swatch
                Child = new TextBlock
                {
                    Text = active ? "\uE73E" : "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Theme.Luminance(color) > 0.5 ? Colors.Black : Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var ring = new Border
            {
                Width = 34, Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = active ? new SolidColorBrush(color) : Brushes.Transparent,
                Child = dot,
            };
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchBtn"),
                Margin = new Thickness(0, 0, 7, 7),
                ToolTip = name,
                Content = ring,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, name);
            swatch.Click += (_, _) => SetAccent(hex);
            AccentSwatches.Children.Add(swatch);
        }

        // custom colour
        var custom = new Button
        {
            Style = (Style)FindResource("IconBtn"),
            Content = "\uE790",
            Width = 34, Height = 34,
            Margin = new Thickness(0, 0, 7, 7),
            ToolTip = L10n.T("settings.accent"),
        };
        System.Windows.Automation.AutomationProperties.SetName(custom, L10n.T("settings.accent"));
        custom.Click += (_, _) =>
        {
            var picked = ColorPickerDialog.Pick(this, App.Settings.AccentHex);
            if (picked is not null) SetAccent(picked);
        };
        AccentSwatches.Children.Add(custom);
    }

    private void SetAccent(string hex)
    {
        App.Settings.AccentHex = hex;
        Theme.ApplyAccent(hex);
        ProfileStore.Save(App.Settings);
        BuildAccentSwatches();
    }

    // ---------- advanced (optional elevation / optional driver) ----------

    /// <summary>
    /// FullRGB runs unelevated by design. This card explains what that costs and offers two
    /// OPT-IN paths, both driven by measured facts from the engine log on this rig:
    ///   * PawnIO driver missing  -> RGB RAM/GPU cannot work at all.
    ///   * driver present         -> the ENGINE still needs to run elevated for SMBus; registering
    ///                               it as a Scheduled Task buys that with one UAC prompt, ever,
    ///                               and keeps this process at normal privilege.
    /// </summary>
    private void RefreshAdvanced()
    {
        if (PawnIoBtn is null) return;
        bool elevated = SDK.Elevation.IsElevated;
        bool driver = Setup.DependencyManager.IsPawnIoInstalled();

        AdminTxt.Text = elevated ? L10n.T("adv.elevated") : L10n.T("adv.normal");

        if (!driver)
        {
            PawnIoTxt.Text = L10n.T("adv.pawnio.missing");
            PawnIoBtn.Content = L10n.T("adv.pawnio.install");
            PawnIoBtn.IsEnabled = true;
            PawnIoBtn.Tag = "install";
        }
        else
        {
            PawnIoTxt.Text = L10n.T("adv.pawnio.ready");
            PawnIoBtn.Content = L10n.T("adv.pawnio.installed");
            PawnIoBtn.IsEnabled = false;
            PawnIoBtn.Tag = null;
        }

        // ---- RGB RAM row ----
        string engineExe = SDK.OpenRgbProcessManager.DefaultExePath();
        bool task = Setup.EngineTask.IsRegistered();
        bool taskMine = task && Setup.EngineTask.MatchesInstall(engineExe);
        bool ramFound = _client?.Controllers.Any(c => c.Kind == SDK.RgbDeviceType.DRAM) == true;

        if (!driver)
        {
            RamTxt.Text = L10n.T("adv.ram.needDriver");
            RamBtn.Content = L10n.T("adv.ram.enable");
            RamBtn.IsEnabled = false;
            RamBtn.Tag = null;
        }
        else if (task && !taskMine)
        {
            // A task from a different install points at another engine folder; re-registering is
            // the honest fix, not pretending RAM is enabled.
            RamTxt.Text = L10n.T("adv.ram.stale");
            RamBtn.Content = L10n.T("adv.ram.repoint");
            RamBtn.IsEnabled = true;
            RamBtn.Tag = "register";
        }
        else if (!task)
        {
            RamTxt.Text = L10n.T("adv.ram.off");
            RamBtn.Content = L10n.T("adv.ram.enable");
            RamBtn.IsEnabled = true;
            RamBtn.Tag = "register";
        }
        else
        {
            // Task exists. Say whether it actually produced DRAM controllers — claiming success
            // without checking would be the same lie the old status line told.
            RamTxt.Text = ramFound ? L10n.T("adv.ram.on") : L10n.T("adv.ram.onNoRam");
            RamBtn.Content = L10n.T("adv.ram.disable");
            RamBtn.IsEnabled = true;
            RamBtn.Tag = "unregister";
        }
    }

    /// <summary>
    /// Registers/removes the elevated engine task, then restarts the engine through it so the
    /// result is visible immediately (RAM appears in the device list) instead of after a relaunch.
    /// </summary>
    private async void EngineTask_Click(object sender, RoutedEventArgs e)
    {
        string? mode = RamBtn.Tag as string;
        if (mode is null) return;

        RamBtn.IsEnabled = false;
        try
        {
            if (mode == "register")
            {
                if (!ConfirmDialog.Ask(this, L10n.T("adv.ram.confirm"), L10n.T("adv.ram.enable")))
                    return;

                string exe = SDK.OpenRgbProcessManager.DefaultExePath();
                string error = "";
                bool ok = await Task.Run(() =>
                    Setup.EngineTask.Register(exe, App.Settings.ServerPort, out error));
                if (!ok)
                {
                    SetStatus(error == "cancelled"
                        ? L10n.T("adv.ram.cancelled")
                        : L10n.T("status.failed", error), StatusKind.Warn);
                    return;
                }
            }
            else
            {
                string error = "";
                if (!await Task.Run(() => Setup.EngineTask.Unregister(out error)) && error != "cancelled")
                {
                    SetStatus(L10n.T("status.failed", error), StatusKind.Warn);
                    return;
                }
                // The elevated engine survives task removal and keeps holding the SDK port, so
                // FullRGB would re-attach to it and the change would look like a no-op.
                // Verified: a normal-user Stop-Process on it returns "Access is denied".
                await Task.Run(() => Setup.EngineTask.StopElevatedEngine(out _));
            }

            await RestartEngineAsync();
        }
        finally
        {
            RefreshAdvanced();
        }
    }

    /// <summary>
    /// Tears the session down and brings it back up through whatever launch path is now
    /// configured. Used after the engine task is added or removed.
    /// </summary>
    private async Task RestartEngineAsync()
    {
        SetStatus(L10n.T("status.starting"), StatusKind.Busy);
        _engine?.Stop();
        _engine?.Dispose();
        _engine = null;
        _client?.Dispose();
        _client = null;

        // An engine started from the task runs elevated and cannot be killed from here; ask it to
        // exit via its own process only when we own it.
        _mgr?.Stop();
        _mgr = null;

        // Give a previously-elevated engine time to release the SDK port before reconnecting.
        await Task.Delay(1500);
        await ConnectAsync();
    }

    private async void InstallPawnIo_Click(object sender, RoutedEventArgs e)
    {
        if (PawnIoBtn.Tag is not "install") return;

        var dep = Setup.DependencyManager.All.First(d => d.Id == "pawnio");
        PawnIoBtn.IsEnabled = false;
        DepBar.Visibility = Visibility.Visible;
        DepTxt.Visibility = Visibility.Visible;
        DepTxt.Text = L10n.T("scan.step.download", dep.Name);

        var progress = new Setup.DependencyProgress { Id = dep.Id, Name = dep.Name };
        bool ok = await Setup.DependencyManager.InstallAsync(dep, progress, p => Dispatcher.Invoke(() =>
        {
            if (p.Stage == "download")
            {
                DepBar.IsIndeterminate = p.Percent < 0;
                if (p.Percent >= 0) DepBar.Value = p.Percent;
                DepTxt.Text = p.BytesTotal > 0
                    ? $"{L10n.T("scan.step.download", dep.Name)} — {p.BytesReceived / 1048576.0:F1} / {p.BytesTotal / 1048576.0:F1} MB"
                    : L10n.T("scan.step.download", dep.Name);
            }
            else if (p.Stage == "install")
            {
                DepBar.IsIndeterminate = true;
                DepTxt.Text = L10n.T("scan.step.install", dep.Name);
            }
        }));

        DepBar.IsIndeterminate = false;
        DepBar.Visibility = Visibility.Collapsed;
        DepTxt.Text = ok ? L10n.T("scan.reboot") : L10n.T("scan.dep.failed", dep.Name, progress.Error);
        RefreshAdvanced();
    }

    // ---------- startup ----------

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        bool want = AutostartChk.IsChecked == true;
        App.Settings.StartWithWindows = want;
        if (!Autostart.Set(want, App.Settings.StartMinimized ? "--minimized" : ""))
            SetStatus(L10n.T("status.failed", "schtasks"), StatusKind.Error);
        ProfileStore.Save(App.Settings);
    }

    private void Minimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.StartMinimized = MinimizedChk.IsChecked == true;
        // The scheduled task embeds the argument, so it has to be rewritten when this flips.
        if (App.Settings.StartWithWindows)
            Autostart.Set(true, App.Settings.StartMinimized ? "--minimized" : "");
        ProfileStore.Save(App.Settings);
    }

    private void AutoFx_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.AutoStartEffects = AutoFxChk.IsChecked == true;
        ProfileStore.Save(App.Settings);
    }

    private void CloseEngine_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.CloseEngineOnExit = CloseEngineChk.IsChecked == true;
        ProfileStore.Save(App.Settings);
    }
}

/// <summary>
/// Sets the "Start with Windows" scheduled task. Runs at the user's NORMAL level
/// (`/RL LIMITED`): FullRGB does not need admin, and `/RL HIGHEST` would make the task
/// silently fail to start on a standard account.
/// </summary>
public static class Autostart
{
    private const string TaskName = "FullRGB";

    public static string BuildCommand(bool enable, string exe, string args)
        => enable
            ? $"/Create /F /TN {TaskName} /SC ONLOGON /RL LIMITED /TR \"\\\"{exe}\\\" {args}\""
            : $"/Delete /F /TN {TaskName}";

    public static bool Set(bool enable, string args)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;
            var cmd = BuildCommand(enable, exe, args);
            using var p = Process.Start(new ProcessStartInfo("schtasks", cmd)
            { CreateNoWindow = true, UseShellExecute = false });
            if (p is null) return false;
            // schtasks is instant; waiting lets us report a real failure instead of guessing.
            p.WaitForExit(4000);
            return p.HasExited ? p.ExitCode == 0 : true;
        }
        catch { return false; }
    }
}

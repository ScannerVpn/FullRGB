using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    private bool _rescanning;

    private async Task RescanAsync()
    {
        if (_rescanning) return; // StatusPill Border still raises clicks when IsEnabled=false
        _rescanning = true;
        StatusPill.IsEnabled = false;
        StatusPill.IsHitTestVisible = false;
        if (_client is null) { await ConnectAsync(); _rescanning = false; StatusPill.IsEnabled = true; StatusPill.IsHitTestVisible = true; return; }
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
            StatusPill.IsHitTestVisible = true;
            _rescanning = false;
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
        SchedChk.IsChecked = App.Settings.SchedulerEnabled;
        BuildSchedMinutes();
        FgChk.IsChecked = App.Settings.ForegroundEnabled;
        FgMapBox.Text = string.Join("\n", App.Settings.ForegroundMap.Select(kv => $"{kv.Key}={kv.Value}"));
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
        ResetSchedCountdown(); // a manual switch restarts the rotation countdown
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
        try { _engine?.Stop(); } catch { }
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        try { _client?.Dispose(); } catch { }
        _client = null;

        // An engine started from the task runs elevated and cannot be killed from here; ask it to
        // exit via its own process only when we own it.
        try { _mgr?.Stop(); } catch { }
        _mgr = null;

        // Give a previously-elevated engine time to release the SDK port before reconnecting.
        await Task.Delay(1500);
        if (_reallyExiting || !IsLoaded) return; // closed during restart: don't resurrect
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
        AutostartChk.IsEnabled = false;
        _ = Task.Run(() =>
        {
            bool ok = Autostart.Set(want, App.Settings.StartMinimized ? "--minimized" : "");
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    AutostartChk.IsEnabled = true;
                    if (!ok) SetStatus(L10n.T("status.failed", "schtasks"), StatusKind.Error);
                    ProfileStore.Save(App.Settings);
                }
                catch { }
            });
        });
    }

    private void Minimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.StartMinimized = MinimizedChk.IsChecked == true;
        // The scheduled task embeds the argument, so it has to be rewritten when this flips.
        if (App.Settings.StartWithWindows)
            _ = Task.Run(() => Autostart.Set(true, App.Settings.StartMinimized ? "--minimized" : ""));
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

    // ---------- rotation scheduler ----------

    private static readonly double[] SchedOptions = { 1, 5, 10, 15, 30, 60, 120 };

    private void BuildSchedMinutes()
    {
        SchedCmb.Items.Clear();
        foreach (var m in SchedOptions) SchedCmb.Items.Add(L10n.T("sched.minutes", m));
        int best = 0;
        for (int i = 0; i < SchedOptions.Length; i++)
            if (Math.Abs(SchedOptions[i] - App.Settings.SchedulerMinutes) < Math.Abs(SchedOptions[best] - App.Settings.SchedulerMinutes))
                best = i;
        SchedCmb.SelectedIndex = best;
    }

    private void Sched_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.SchedulerEnabled = SchedChk.IsChecked == true;
        ResetSchedCountdown();
        ProfileStore.Save(App.Settings);
    }

    private void SchedCmb_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || SchedCmb.SelectedIndex < 0) return;
        App.Settings.SchedulerMinutes = SchedOptions[Math.Clamp(SchedCmb.SelectedIndex, 0, SchedOptions.Length - 1)];
        ResetSchedCountdown();
        ProfileStore.Save(App.Settings);
    }

    /// <summary>Next profile in nav order, wrapping around (-1 when fewer than two).</summary>
    internal static int SchedulerNextIndex(IList<string> names, string active)
    {
        if (names.Count <= 1) return -1;
        int at = -1;
        for (int i = 0; i < names.Count; i++)
            if (names[i] == active) { at = i; break; }
        return (at + 1) % names.Count;
    }

    private void ResetSchedCountdown()
        => _nextSchedSwitch = DateTime.UtcNow + TimeSpan.FromMinutes(App.Settings.SchedulerMinutes);

    private void SchedTick()
    {
        if (!App.Settings.SchedulerEnabled || App.Settings.Profiles.Count <= 1) return;
        if (DateTime.UtcNow < _nextSchedSwitch) return;
        var names = App.Settings.Profiles.Select(p => p.Name).ToList();
        int next = SchedulerNextIndex(names, CurrentProfile().Name);
        if (next >= 0) SwitchProfile(names[next]);
        ResetSchedCountdown();
    }

    // ---------- per-app profiles ----------

    private void Fg_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.ForegroundEnabled = FgChk.IsChecked == true;
        _autoSwitched = false;
        ProfileStore.Save(App.Settings);
    }

    private void FgMap_Save(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.ForegroundMap = ForegroundWatcher.ParseMap(FgMapBox.Text);
        FgMapBox.Text = string.Join("\n", App.Settings.ForegroundMap.Select(kv => $"{kv.Key}={kv.Value}"));
        _autoSwitched = false;
        ProfileStore.Save(App.Settings);
    }

    private void FgTick()
    {
        if (!App.Settings.ForegroundEnabled || App.Settings.ForegroundMap.Count == 0) return;
        string? want;
        try { want = ForegroundWatcher.MatchProfile(App.Settings.ForegroundMap, ForegroundWatcher.CurrentExe(), App.Settings.Profiles.Select(p => p.Name)); }
        catch { return; }
        string active = CurrentProfile().Name;
        if (want is not null)
        {
            if (want != active)
            {
                if (!_autoSwitched) { _manualProfile = active; _autoSwitched = true; }
                SwitchProfile(want);
            }
        }
        else if (_autoSwitched)
        {
            _autoSwitched = false;
            if (_manualProfile is not null && App.Settings.Profiles.Any(p => p.Name == _manualProfile))
                SwitchProfile(_manualProfile);
        }
    }

    // ---------- settings backup ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "JSON|*.json",
                FileName = "fullrgb-settings.json",
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            ProfileStore.SaveTo(dlg.FileName, App.Settings);
            SetStatus(L10n.T("backup.done", dlg.FileName), StatusKind.Ok);
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new System.Windows.Forms.OpenFileDialog { Filter = "JSON|*.json" };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var loaded = ProfileStore.LoadFrom(dlg.FileName);
            if (loaded.Profiles.Count == 0) { SetStatus(L10n.T("backup.invalid"), StatusKind.Error); return; }
            ProfileStore.BackupLatest();
            App.Settings = loaded;
            L10n.Set(App.Settings.Language);
            Theme.ApplyAccent(App.Settings.AccentHex);
            ApplyLanguage();
            LoadProfileToUi();
            BuildDeviceList();
            BuildDevicePicker();
            BuildEffectEditor();
            RefreshStatus();
            ProfileStore.Save(App.Settings);
            SetStatus(L10n.T("backup.restored"), StatusKind.Ok);
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
    }
}

/// <summary>Reads the focused app's exe name + parses the exe→profile map (testable, no UI).</summary>
internal static class ForegroundWatcher
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Test hook: when set, CurrentExe returns this instead of calling Win32.</summary>
#pragma warning disable CS0649
    internal static Func<string?>? ExeOverride;
#pragma warning restore CS0649

    internal static string? CurrentExe()
    {
        if (ExeOverride is not null) return ExeOverride();
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { return p.ProcessName + ".exe"; }
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }
        catch { return null; }
    }

    /// <summary>Parses "exe=Profile" lines (blanks and # comments ignored, last wins).</summary>
    internal static Dictionary<string, string> ParseMap(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string exe = line[..eq].Trim(), prof = line[(eq + 1)..].Trim();
            if (exe.Length == 0 || prof.Length == 0) continue;
            d[exe] = prof;
        }
        return d;
    }

    /// <summary>Profile mapped to the focused exe, or null (unknown exe / unknown profile).</summary>
    internal static string? MatchProfile(Dictionary<string, string>? map, string? exe, IEnumerable<string> profiles)
    {
        if (map is null || string.IsNullOrEmpty(exe)) return null;
        var set = new HashSet<string>(profiles, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
            if (exe.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && set.Contains(kv.Value))
                return kv.Value;
        return null;
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
    /// <summary>
    /// PowerShell that (re)creates the logon task via the ScheduledTasks module — NOT
    /// `schtasks /Create`: schtasks splits its /TR value at the first space even when quoted,
    /// so any exe under a spaced folder ("RGB Control") registered as Command=`G:\Ai\RGB`
    /// and never ran. Separate -Execute/-Argument fields have no such parsing.
    /// </summary>
    public static string BuildRegisterScript(string exePath, string args)
    {
        string dir = System.IO.Path.GetDirectoryName(exePath) ?? "";
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        string user = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USERDOMAIN"))
            ? Environment.UserName
            : Environment.GetEnvironmentVariable("USERDOMAIN") + "\\" + Environment.UserName;
        string argLine = string.IsNullOrWhiteSpace(args) ? "" : $" -Argument {Q(args.Trim())}";

        return $@"$ErrorActionPreference = 'Stop'
$action = New-ScheduledTaskAction -Execute {Q(exePath)}{argLine} -WorkingDirectory {Q(dir)}
$trigger = New-ScheduledTaskTrigger -AtLogOn -User {Q(user)}
$principal = New-ScheduledTaskPrincipal -UserId {Q(user)} -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName {Q(TaskName)} -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Description 'Runs FullRGB at logon.' -Force | Out-Null
exit 0
";
    }

    public static string BuildUnregisterScript()
        => $@"$ErrorActionPreference = 'SilentlyContinue'
Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false
exit 0
";

    public static bool Set(bool enable, string args) => Set(enable, args, out _);

    public static bool Set(bool enable, string args, out string error)
    {
        error = "";
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) { error = "no process path"; return false; }
            string script = enable ? BuildRegisterScript(exe, args) : BuildUnregisterScript();
            if (RunScript(script, false, out error)) return true;
            // A task left behind with RunLevel=HighestAvailable cannot be touched without
            // elevation ("Access is denied") — repair it once, elevated; the replacement is
            // LeastPrivilege so this never recurs.
            if (error.Contains("denied", StringComparison.OrdinalIgnoreCase) && !SDK.Elevation.IsElevated)
                return Setup.EngineTask.RunElevatedScript(script, out error);
            return false;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    /// <summary>Runs a task script unelevated via powershell -File (temp file, like the engine task).</summary>
    private static bool RunScript(string script, bool elevated, out string error)
    {
        error = "";
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fullrgb-autostart-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"",
                UseShellExecute = elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = !elevated,
                RedirectStandardError = !elevated,
            };
            if (elevated) psi.Verb = "runas";
            using var p = Process.Start(psi);
            if (p is null) { error = "could not start powershell"; return false; }
            string std = "", err = "";
            if (!elevated)
            {
                std = p.StandardOutput.ReadToEnd();
                err = p.StandardError.ReadToEnd();
            }
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } error = "task script timed out"; return false; }
            if (p.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(err) ? std.Trim() : err.Trim();
                if (string.IsNullOrWhiteSpace(error)) error = $"exit code {p.ExitCode}";
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            error = "cancelled";
            return false;
        }
        catch (Exception e) { error = e.Message; return false; }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Exe path the logon task will actually run, or null when it does not exist.</summary>
    public static string? RegisteredExePath()
    {
        var xml = QueryXml();
        return xml is null ? null : Setup.EngineTask.ParseCommand(xml);
    }

    /// <summary>True when the logon task exists AND points at this installation's exe.</summary>
    public static bool MatchesCurrent()
    {
        var exe = Environment.ProcessPath;
        var registered = RegisteredExePath();
        return exe is not null && registered is not null
            && string.Equals(System.IO.Path.GetFullPath(registered), System.IO.Path.GetFullPath(exe),
                             StringComparison.OrdinalIgnoreCase);
    }

    public static bool EnsureCurrent(string args) => EnsureCurrent(args, out _);

    /// <summary>
    /// Rewrites the logon task to this exe when it is missing or points at an old folder.
    /// The task stores an ABSOLUTE path, so every move to a new dist folder silently broke
    /// autostart (the task kept launching a deleted exe → 0x80070002) until the user
    /// toggled the setting off and on. Own-user task: no UAC needed.
    /// </summary>
    public static bool EnsureCurrent(string args, out string error)
    {
        error = "";
        try
        {
            if (MatchesCurrent()) return true;
            return Set(true, args, out error);
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    private static string? QueryXml()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /XML",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            // same quirk as the engine task: declared UTF-16, actually single-byte when redirected
            using var ms = new System.IO.MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!copy.Wait(TimeSpan.FromSeconds(8))) { try { p.Kill(); } catch { } return null; }
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
            if (p.ExitCode != 0) return null;
            var bytes = ms.ToArray();
            foreach (var enc in new System.Text.Encoding[]
                     { System.Text.Encoding.UTF8, System.Text.Encoding.Unicode, System.Text.Encoding.Default })
            {
                string text = enc.GetString(bytes);
                if (text.Contains("<Command>", StringComparison.Ordinal)) return text;
            }
            return null;
        }
        catch { return null; }
    }
}

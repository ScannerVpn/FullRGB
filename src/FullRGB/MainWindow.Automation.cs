using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FullRGB.Automation;
using FullRGB.Config;
using FullRGB.Effects;
using FullRGB.SDK;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace FullRGB;

/// <summary>
/// Round 21 automation surface, one partial for all of it:
///  • IControlTarget — the command set the CLI, the named pipe, the HTTP API and the mobile
///    companion all drive (see <see cref="CommandDispatcher"/>).
///  • ControlHub lifecycle — pipe + local HTTP, started with the window, stopped on exit.
///  • Time schedule — profile rules by clock time ("22:00-07:00=Night"), checked every 30 s.
///  • Engine safe mode — the crash-loop detector in <see cref="Setup.EngineSafeMode"/> plus the
///    banner and the backoff retry loop.
///  • App auto-update — check (24 h cadence) / download / the pending-swap hint in Settings.
/// </summary>
public partial class MainWindow : IControlTarget
{
    private ControlHub? _hub;
    private readonly Setup.EngineSafeMode _safeMode = new();
    private DispatcherTimer? _safeModeTimer;
    private DispatcherTimer? _timeSchedTimer;
    private string? _lastTimeSchedProfile;
    private Update.UpdateInfo? _updateInfo;
    private DispatcherTimer? _updateTimer;

    // ---------------------------------------------------------------- control hub

    private void StartHub()
    {
        if (_hub is not null || Headless) return;
        try
        {
            if (!App.Settings.ControlApiEnabled) return;
            _hub = ControlHub.Start(Dispatcher, this, App.Settings.ControlApiPort, App.Settings.CompanionEnabled);
        }
        catch (Exception e)
        {
            Diag.AppLog.Exception("control hub", e);
        }
    }

    private void StopHub()
    {
        try { _hub?.Dispose(); } catch { }
        _hub = null;
    }

    /// <summary>LAN URLs for the companion card (empty when the machine has no IPv4).</summary>
    internal static List<string> LanUrls(int port)
    {
        var urls = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;   // APIPA, not reachable
                    urls.Add($"http://{ip}:{port}/");
                }
            }
        }
        catch { }
        return urls.Distinct().ToList();
    }

    private void RefreshCompanionCard()
    {
        if (CompanionInfoTxt is null) return;
        if (!App.Settings.CompanionEnabled)
        {
            CompanionInfoTxt.Text = "";
            return;
        }
        int port = App.Settings.ControlApiPort;
        var urls = LanUrls(port);
        string list = urls.Count > 0 ? string.Join("   ", urls) : $"http://127.0.0.1:{port}/";
        CompanionInfoTxt.Text = $"{list}\nPIN: {_hub?.CompanionPin ?? "(restart to show)"}";
    }

    private void Companion_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.CompanionEnabled = CompanionChk.IsChecked == true;
        ProfileStore.Save(App.Settings);
        // The listener's bind scope changes with this flag: restart the hub.
        StopHub();
        StartHub();
        RefreshCompanionCard();
        SetStatus(L10n.T("companion.toggled"), StatusKind.Info);
    }

    private void CompanionOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string url = $"http://127.0.0.1:{App.Settings.ControlApiPort}/";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
    }

    private void CompanionCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var urls = LanUrls(App.Settings.ControlApiPort);
            string text = (urls.Count > 0 ? urls[0] : $"http://127.0.0.1:{App.Settings.ControlApiPort}/")
                          + $"  PIN: {_hub?.CompanionPin ?? "-"}";
            System.Windows.Clipboard.SetText(text);
            SetStatus(L10n.T("companion.copied"), StatusKind.Ok);
        }
        catch (Exception ex) { SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error); }
    }

    // ---------------------------------------------------------------- CLI one-shots

    /// <summary>
    /// A CLI automation verb (<c>--set-profile gaming</c>) that arrived when NO instance was
    /// running is parked here by App.OnStartup and replayed once the engine session is up —
    /// the command's effect is identical whether the app was running or not.
    /// </summary>
    private void ApplyPendingCommand()
    {
        string verb = App.PendingCommandVerb;
        if (string.IsNullOrEmpty(verb)) return;
        App.PendingCommandVerb = "";
        try
        {
            string reply = CommandDispatcher.Execute(this, verb);
            Diag.AppLog.Info($"pending CLI '{verb}' -> {reply.Split('\n')[0]}");
            SetStatus(L10n.T("cli.applied", verb), StatusKind.Info);
        }
        catch (Exception e)
        {
            Diag.AppLog.Warn("pending CLI failed: " + e.Message);
        }
    }

    // ---------------------------------------------------------------- IControlTarget

    /// <summary>Effect-type name map shared by the dispatcher, the HTTP API and the CLI.</summary>
    internal static readonly Dictionary<string, EffectType> EffectNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["solid"] = EffectType.Solid,
        ["gradient"] = EffectType.Gradient,
        ["rainbow"] = EffectType.Rainbow,
        ["cycle"] = EffectType.ColorCycle,
        ["colorcycle"] = EffectType.ColorCycle,
        ["breathing"] = EffectType.Breathing,
        ["wave"] = EffectType.Wave,
        ["comet"] = EffectType.Comet,
        ["blink"] = EffectType.Blink,
        ["fire"] = EffectType.Fire,
        ["temp"] = EffectType.Temperature,
        ["temperature"] = EffectType.Temperature,
        ["audio"] = EffectType.AudioVU,
        ["music"] = EffectType.AudioVU,
        ["vu"] = EffectType.AudioVU,
        ["custom"] = EffectType.Custom,
        ["spectrum"] = EffectType.Spectrum,
        ["scanner"] = EffectType.Scanner,
        ["sparkle"] = EffectType.Sparkle,
        ["plasma"] = EffectType.Plasma,
        ["ambient"] = EffectType.Ambient,
        ["gaming"] = EffectType.Gaming,
        ["gamepulse"] = EffectType.GamePulse,
        ["game"] = EffectType.GamePulse,
    };

    internal static string EffectNameOf(EffectType t) => EffectNames
        .FirstOrDefault(kv => kv.Value == t && !kv.Key.StartsWith("colorcycle") && !kv.Key.StartsWith("game"))
        .Key is { } name && name.Length > 0
        ? name
        : t.ToString().ToLowerInvariant();

    string IControlTarget.StatusJson()
    {
        var p = CurrentProfile();
        var effect = p.GlobalEffect;
        var controllers = _client?.Controllers ?? new List<RgbController>();
        int leds = controllers.Sum(c => (int)c.Zones.Sum(z => z.LedsCount));
        string engine = _client is { Connected: true } ? "connected" : "offline";
        string safe = _safeMode.Active ? ",\"safeMode\":true" : "";
        string Escape(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? "");
        // numbers go through InvariantCulture: fa-IR uses "/" as its decimal separator, which
        // would produce invalid JSON on a Persian-locale machine
        string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return "{" +
               $"\"ok\":true," +
               $"\"app\":\"FullRGB\"," +
               $"\"version\":\"{Update.AppUpdater.CurrentVersion()}\"," +
               $"\"profile\":{Escape(p.Name)}," +
               $"\"effect\":{Escape(EffectNameOf(effect.Type))}," +
               $"\"color\":{Escape(effect.ColorHex)}," +
               $"\"color2\":{Escape(effect.Color2Hex)}," +
               $"\"brightness\":{Num(effect.Brightness)}," +
               $"\"speed\":{Num(effect.Speed)}," +
               $"\"power\":{(_engine?.IsRunning == true).ToString().ToLowerInvariant()}," +
               $"\"devices\":{controllers.Count}," +
               $"\"leds\":{leds}," +
               $"\"engine\":\"{engine}\"," +
               $"\"profiles\":[{string.Join(",", App.Settings.Profiles.Select(x => Escape(x.Name)))}]," +
               $"\"game\":{{\"lastEvent\":{Escape(Sensors.GameEventState.LastEvent)}" +
               $",\"health\":{(!Sensors.GameEventState.HasHealth ? "null" : Num(Sensors.GameEventState.Health))}}}" +
               $"{safe}" +
               "}";
    }

    List<string> IControlTarget.ProfileNames() =>
        App.Settings.Profiles.Select(p => p.Name).ToList();

    bool IControlTarget.SetProfile(string name)
    {
        if (!App.Settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        var actual = App.Settings.Profiles.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Name;
        _lastTimeSchedProfile = null;   // an explicit switch cancels a schedule hold
        SwitchProfile(actual);
        Diag.AppLog.Info("control bus: set-profile " + actual);
        return true;
    }

    bool IControlTarget.SetEffect(string name)
    {
        if (!EffectNames.TryGetValue(name, out var type)) return false;
        var p = CurrentProfile();
        p.GlobalEffect.Type = type;
        p.GlobalEffect.Normalized();
        PushEdit();
        _engine?.Apply(p);
        ProfileStore.Save(App.Settings);
        LoadProfileToUi();
        BuildEffectEditor();
        BuildDeviceList();
        Diag.AppLog.Info("control bus: set-effect " + name);
        return true;
    }

    bool IControlTarget.SetColor(string hex, bool secondary)
    {
        if (!AppSettings.IsHexColor(hex)) return false;
        var p = CurrentProfile();
        if (secondary) p.GlobalEffect.Color2Hex = hex;
        else p.GlobalEffect.ColorHex = hex;
        _engine?.Apply(p);
        ProfileStore.Save(App.Settings);
        BuildEffectEditor();
        return true;
    }

    bool IControlTarget.SetBrightness(double v)
    {
        var p = CurrentProfile();
        p.GlobalEffect.Brightness = Math.Clamp(v, 0, 1);
        _engine?.Apply(p);
        ProfileStore.Save(App.Settings);
        BuildEffectEditor();
        return true;
    }

    bool IControlTarget.SetSpeed(double v)
    {
        var p = CurrentProfile();
        p.GlobalEffect.Speed = Math.Clamp(v, 0, 1);
        _engine?.Apply(p);
        ProfileStore.Save(App.Settings);
        BuildEffectEditor();
        return true;
    }

    bool IControlTarget.Power(bool on)
    {
        var engine = _engine;
        if (engine is null) return false;
        if (on)
        {
            if (!engine.IsRunning) engine.Apply(CurrentProfile());
        }
        else engine.Stop();
        SyncRunButtons();
        Diag.AppLog.Info("control bus: power " + (on ? "on" : "off"));
        return true;
    }

    bool IControlTarget.Blackout()
    {
        try
        {
            _engine?.Blackout();
            SetStatus(L10n.T("status.stopped"), StatusKind.Info);
            return true;
        }
        catch { return false; }
    }

    void IControlTarget.PushGameEvent(string name, double value)
    {
        Sensors.GameEventState.Push(name, value);
        Diag.AppLog.Info($"control bus: game-event {name} {value}");
    }

    bool IControlTarget.Rescan()
    {
        if (_client is not { Connected: true }) return false;
        _ = RescanAsync();
        return true;
    }

    string IControlTarget.CompanionPin => _hub?.CompanionPin ?? "";
    string IControlTarget.ApiToken => _hub?.ApiToken ?? "";

    // ---------------------------------------------------------------- time schedule

    private void StartTimeSchedule()
    {
        if (_timeSchedTimer is not null || Headless) return;
        _timeSchedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timeSchedTimer.Tick += (_, _) => { try { TimeSchedTick(); } catch { } };
        _timeSchedTimer.Start();
    }

    /// <summary>Applies the clock rules. Manual/schedule interplay mirrors the per-app logic:
    /// the schedule wins while a rule matches; when no rule matches any more, the profile that
    /// was active before the rule took over comes back.</summary>
    internal void TimeSchedTick()
    {
        if (!App.Settings.TimeScheduleEnabled || Headless) return;
        var (rules, errors) = ScheduleRules.Parse(App.Settings.TimeScheduleRules);
        if (errors.Count > 0 && rules.Count == 0) return;
        string? want = ScheduleRules.Evaluate(rules, DateTime.Now);
        if (want is not null)
        {
            var match = App.Settings.Profiles.FirstOrDefault(p =>
                p.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (match is null) return;
            string active = CurrentProfile().Name;
            if (match.Name != active)
            {
                if (_lastTimeSchedProfile is null) _lastTimeSchedProfile = active;
                SwitchProfile(match.Name);
                Diag.AppLog.Info($"time schedule: {match.Name}");
            }
        }
        else if (_lastTimeSchedProfile is not null)
        {
            string back = _lastTimeSchedProfile;
            _lastTimeSchedProfile = null;
            if (App.Settings.Profiles.Any(p => p.Name == back)
                && back != CurrentProfile().Name)
                SwitchProfile(back);
        }
    }

    private void TimeSched_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.TimeScheduleEnabled = TimeSchedChk.IsChecked == true;
        _lastTimeSchedProfile = null;
        ProfileStore.Save(App.Settings);
    }

    private void TimeSched_Save(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.TimeScheduleRules = TimeSchedBox.Text ?? "";
        var (rules, errors) = ScheduleRules.Parse(App.Settings.TimeScheduleRules);
        // Echo the parsed rules back so the user sees exactly what was understood.
        TimeSchedBox.Text = errors.Count > 0
            ? string.Join("\n", rules.Select(r => r.Describe())
                .Concat(errors.Select(x => "# ? " + x)))
            : string.Join("\n", rules.Select(r => r.Describe()));
        ProfileStore.Save(App.Settings);
        _lastTimeSchedProfile = null;
    }

    /// <summary>
    /// Saves and re-binds the system-wide hotkeys. Re-binding rather than merging means an emptied
    /// box actually releases the key, which is the only way to undo a bad binding.
    /// </summary>
    private void HotkeyApply_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        try
        {
            App.Settings.HotkeyToggleEffects = (HotkeyToggleBox.Text ?? "").Trim();
            App.Settings.HotkeyBlackout = (HotkeyBlackoutBox.Text ?? "").Trim();
            App.Settings.HotkeyNextProfile = (HotkeyNextBox.Text ?? "").Trim();
            // Echo the accepted combos back: a typo would otherwise look like a working shortcut.
            HotkeyToggleBox.Text = App.Settings.HotkeyToggleEffects;
            HotkeyBlackoutBox.Text = App.Settings.HotkeyBlackout;
            HotkeyNextBox.Text = App.Settings.HotkeyNextProfile;
            ProfileStore.Save(App.Settings);
            ApplyHotkeys();
            SetStatus(L10n.T("hotkey.applied"), StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus(L10n.T("status.failed", ex.Message), StatusKind.Error);
        }
    }

    // ---------------------------------------------------------------- safe mode

    /// <summary>Records one full engine replacement; 3 within 10 minutes trip safe mode.</summary>
    private void NoteEngineReplaced()
    {
        _safeMode.NoteEngineReplaced();
        if (_safeMode.Active || !_safeMode.IsUnstable()) return;
        EnterSafeMode();
    }

    private void EnterSafeMode()
    {
        if (_safeMode.Active || Headless) return;
        _safeMode.Enter(CurrentProfile().Name);

        // Park the LEDs on a calm static colour — the accent, heavily dimmed. Never animate:
        // an animated effect in safe mode would look like "still working".
        var safe = new Profile
        {
            Name = "__safemode__",
            GlobalEffect = new EffectDef
            {
                Type = EffectType.Solid,
                ColorHex = string.Equals(App.Settings.AccentHex, "#000000", StringComparison.OrdinalIgnoreCase)
                    ? "#12324A" : App.Settings.AccentHex,
                Brightness = 0.12,
                SyncZones = true,
            },
        };
        try { _engine?.Apply(safe); } catch { }

        SafeModeTxt.Text = L10n.T("safemode.text", _safeMode.SecondsUntilRetry);
        SafeModeRetryBtn.Content = L10n.T("safemode.restore");
        SafeModeBanner.Visibility = Visibility.Visible;
        Diag.AppLog.Warn("safe mode active: lighting parked on a dim static colour");

        _safeModeTimer ??= NewSafeModeTimer();
        _safeModeTimer!.Interval = TimeSpan.FromSeconds(Math.Max(10, _safeMode.SecondsUntilRetry));
        _safeModeTimer.Start();
    }

    private DispatcherTimer? NewSafeModeTimer()
    {
        var t = new DispatcherTimer();
        t.Tick += async (_, _) =>
        {
            t.Stop();
            if (_safeMode.Active && !_reallyExiting && !_osShuttingDown)
            {
                SafeModeTxt.Text = L10n.T("safemode.trying");
                bool ok = await TryRestoreLightingAsync(wantPaint: true);
                if (ok)
                {
                    ExitSafeMode();
                }
                else
                {
                    int delay = _safeMode.ArmNextRetry();
                    SafeModeTxt.Text = L10n.T("safemode.text", delay);
                    t.Interval = TimeSpan.FromSeconds(delay);
                    t.Start();
                }
            }
        };
        return t;
    }

    private void ExitSafeMode()
    {
        string? restore = _safeMode.Exit();
        SafeModeBanner.Visibility = Visibility.Collapsed;
        _safeModeTimer?.Stop();
        if (!string.IsNullOrEmpty(restore)
            && App.Settings.Profiles.Any(p => p.Name == restore)
            && restore != CurrentProfile().Name)
            SwitchProfile(restore);
        RefreshStatus();
    }

    /// <summary>A healthy connect/watchdog-motion reading proves the engine is fine again.</summary>
    private void OnSessionHealthy()
    {
        if (_safeMode.Active) ExitSafeMode();
        else _safeMode.Reset();
    }

    private void SafeModeRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_safeModeTimer is not null) _safeModeTimer.Stop();
        SafeModeTxt.Text = L10n.T("safemode.trying");
        _ = SafeModeRetryNowAsync();
    }

    private async Task SafeModeRetryNowAsync()
    {
        bool ok = await TryRestoreLightingAsync(wantPaint: true);
        if (ok) ExitSafeMode();
        else
        {
            int delay = _safeMode.ArmNextRetry();
            SafeModeTxt.Text = L10n.T("safemode.text", delay);
            if (_safeModeTimer is not null)
            {
                _safeModeTimer.Interval = TimeSpan.FromSeconds(delay);
                _safeModeTimer.Start();
            }
        }
    }

    // ---------------------------------------------------------------- auto-update

    private void StartUpdater()
    {
        if (Headless) return;
        RefreshUpdateCard();
        if (!App.Settings.AutoUpdateEnabled) return;
        // Check at most every 24 h; the last successful check is stored in the settings.
        if ((DateTime.UtcNow - App.Settings.LastUpdateCheckUtc).TotalHours < 24) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer!.Stop();
            _updateTimer = null;
            await AutoCheckAsync();
        };
        _updateTimer.Start();
    }

    private async Task AutoCheckAsync()
    {
        try
        {
            var info = await Update.AppUpdater.CheckAsync();
            App.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            ProfileStore.Save(App.Settings);
            if (info is not null)
            {
                _updateInfo = info;
                RefreshUpdateCard();
                SetStatus(L10n.T("update.available", info.Version), StatusKind.Info);
                Diag.AppLog.Info($"update {info.Version} available");
            }
        }
        catch { /* offline: try again tomorrow */ }
    }

    private void RefreshUpdateCard()
    {
        if (UpdateTxt is null) return;
        UpdateChk.IsChecked = App.Settings.AutoUpdateEnabled;
        BetaChk.IsChecked = App.Settings.UpdateBetaChannel;
        UpdateTxt.Text = string.Format(L10n.T("update.version"), Update.AppUpdater.CurrentVersion())
                         + "\n" + (_updateInfo is null
                             ? L10n.T("update.uptodate")
                             : string.Format(L10n.T("update.ready"), _updateInfo.Version));
        UpdateInstallBtn.Visibility = _updateInfo is null ? Visibility.Collapsed : Visibility.Visible;
        if (_updateInfo is not null)
            UpdateInstallBtn.Content = L10n.T("update.install", _updateInfo.Version);
        var pending = Update.AppUpdater.ReadPending();
        if (pending is not null)
            UpdateTxt.Text += "\n" + string.Format(L10n.T("update.pending"), pending.Version);
    }

    private void Update_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        App.Settings.AutoUpdateEnabled = UpdateChk.IsChecked == true;
        // Switching channel invalidates the cached offer: a beta build must not keep showing the
        // stable one (or vice versa) until the next 24 h check.
        bool beta = BetaChk.IsChecked == true;
        if (beta != App.Settings.UpdateBetaChannel) _updateInfo = null;
        App.Settings.UpdateBetaChannel = beta;
        ProfileStore.Save(App.Settings);
        RefreshUpdateCard();
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            SetStatus(L10n.T("update.checking"), StatusKind.Info);
            var info = await Update.AppUpdater.CheckAsync();
            _updateInfo = info;
            RefreshUpdateCard();
            SetStatus(info is null
                ? string.Format(L10n.T("update.none"), Update.AppUpdater.CurrentVersion())
                : string.Format(L10n.T("update.available"), info.Version),
                StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus(L10n.T("update.checkFailed", ex.Message), StatusKind.Error);
        }
        finally { btn.IsEnabled = true; }
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        var info = _updateInfo;
        if (info is null) return;
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            SetStatus(L10n.T("update.downloading"), StatusKind.Busy);
            var pending = await Update.AppUpdater.DownloadAsync(info, p =>
            {
                if (p.Total > 0)
                    SetStatus(string.Format(L10n.T("update.progress"), p.Received * 100.0 / p.Total), StatusKind.Busy);
            });
            SetStatus(L10n.T("update.downloaded", info.Version), StatusKind.Ok);
            Diag.AppLog.Info($"update {info.Version} ready for next start");
            RefreshUpdateCard();

            if (ConfirmDialog.Ask(this,
                    L10n.T("update.restartAsk", info.Version), L10n.T("update.restartNow")))
            {
                // The swap needs THIS exe gone; do it the safe way: relaunch through the
                // swap on next start is the default, but the user asked for now — swap
                // synchronously and let the new instance take over.
                string[] args = { "--minimized" };
                var applied = Update.AppUpdater.ApplyPendingOnStartup(args);
                if (applied is not null)
                {
                    Update.AppUpdater.Relaunch(applied);
                    _reallyExiting = true;
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
                SetStatus(L10n.T("update.swapLater"), StatusKind.Info);
            }
        }
        catch (Exception ex)
        {
            SetStatus(L10n.T("update.checkFailed", ex.Message), StatusKind.Error);
            Diag.AppLog.Exception("update install", ex);
        }
        finally { btn.IsEnabled = true; }
    }
}

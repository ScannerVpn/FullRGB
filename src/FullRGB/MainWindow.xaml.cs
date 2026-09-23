using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullRGB.Config;
using FullRGB.Effects;
using FullRGB.SDK;
using FullRGB.Sensors;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using FlowDirection = System.Windows.FlowDirection;

namespace FullRGB;

/// <summary>
/// Main shell. Split across partial files: this one owns lifecycle, connection, status,
/// banners, tray and language; MainWindow.Devices.cs the device/zone panels;
/// MainWindow.Effects.cs the effect editor and preview; MainWindow.Settings.cs profiles.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>What the effect editor is currently pointed at.</summary>
    private enum TargetMode { Global, Device, Zone }

    private OpenRgbProcessManager? _mgr;
    private OpenRgbClient? _client;
    private EffectEngine? _engine;
    private System.Windows.Threading.DispatcherTimer? _engineWatchdog;
    private readonly TemperatureProvider _temps = new();
    private AudioProvider? _audio;
    private bool _audioFailed;

    private EffectDef _edit = new();
    private TargetMode _target = TargetMode.Global;
    private string? _selectedKey;     // device Key when _target is Device/Zone
    private int _selectedZone = -1;   // zone index when _target is Zone
    private string? _devKey;          // device selected on the Devices tab (independent)
    private bool _loadingUi;
    private TrayController? _tray;
    private bool _reallyExiting;
    private bool _osShuttingDown;
    private Action<string>? _engineStatusHandler;
    // rotation scheduler + per-app profiles
    private System.Windows.Threading.DispatcherTimer? _schedTimer;
    private System.Windows.Threading.DispatcherTimer? _fgTimer;
    private DateTime _nextSchedSwitch = DateTime.UtcNow + TimeSpan.FromMinutes(10);
    private bool _autoSwitched;
    private string? _manualProfile;
    /// <summary>Device inventory sentence for the title subtitle and the tray tooltip.</summary>
    private string _inventory = "";

    public MainWindow() : this(null, null) { }

    /// <summary>
    /// Set by --uitest/--uishot. The Loaded handler must NOT touch hardware in those modes:
    /// showing the window otherwise started OpenRGB (with a UAC prompt) and hung the run.
    /// </summary>
    internal static bool Headless { get; set; }

    public MainWindow(OpenRgbProcessManager? mgr, OpenRgbClient? client)
    {
        InitializeComponent();
        _mgr = mgr;
        _client = client;
        ApplyLanguage();
        Loaded += async (_, _) =>
        {
            try
            {
                StartPreview();   // the hero preview must animate even if the SDK never connects
                if (Headless) { BuildEffectEditor(); LoadProfileToUi(); BuildDevicePicker(); TickPreview(); return; }
                // Arm wake-detection FIRST: one throw in the tray/automation setup below used to
                // skip HookPowerEvents (so the sleep/hibernate repair never armed) and the initial
                // connect — the machine then stayed dark after boot/wake until a manual Rescan.
                HookPowerEvents();
                try { InitTray(); } catch { /* no tray icon is survivable; the session continues */ }
                try { StartAutomation(); } catch { /* rotation/foreground watchers are optional */ }
                try { StartLightingWatchdog(); } catch { /* the frame watchdog is best-effort */ }
                if (_client is { Connected: true }) OnConnected();
                else await ConnectAsync();
            }
            catch (Exception e)
            {
                try { SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error); } catch { }
            }
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        try
        {
            Application.Current.SessionEnding += (_, _) => { _osShuttingDown = true; _reallyExiting = true; };
        }
        catch { }
    }

    // ---------- automation (rotation scheduler + per-app profiles) ----------

    /// <summary>Two cheap polling timers (30 s rotation, 2 s foreground). Started once, UI thread.</summary>
    private void StartAutomation()
    {
        if (_schedTimer is not null) return;
        ResetSchedCountdown();
        _schedTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _schedTimer.Tick += (_, _) => { try { SchedTick(); } catch { } };
        _schedTimer.Start();
        _fgTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _fgTimer.Tick += (_, _) => { try { FgTick(); } catch { } };
        _fgTimer.Start();
    }

    private void StopAutomation()
    {
        try { _schedTimer?.Stop(); } catch { }
        try { _fgTimer?.Stop(); } catch { }
        _schedTimer = null;
        _fgTimer = null;
    }

    // ---------- power resume (sleep / hibernate) ----------

    private bool _powerHooked;
    private bool _resuming;
    private bool _resumePending;
    private Setup.PowerMonitor? _powerMonitor;
    private Setup.LightingWatchdog? _lightingWatchdog;
    /// <summary>When the current session was established (connect or repair): the watchdog's grace.</summary>
    private DateTime _sessionStartedUtc = DateTime.UtcNow;
    /// <summary>Windows terminal-session id this process started in; a change means a fresh logon.</summary>
    private int _sessionIdAtStart = -1;
    private System.Windows.Threading.DispatcherTimer? _resumeHeartbeat;
    private long _lastHeartbeat;
    private long _lastWallClock;
    /// <summary>Heartbeat period; a gap of <see cref="ResumeGapFactor"/> × this means a suspend.</summary>
    private const int HeartbeatSeconds = 5;
    internal const int ResumeGapFactor = 4;

    /// <summary>Seconds since the session was established; the watchdog's grace period.</summary>
    internal double SecondsSinceSession => (DateTime.UtcNow - _sessionStartedUtc).TotalSeconds;

    /// <summary>
    /// After sleep/hibernate the lighting has to be REBUILT: the engine process survives the
    /// suspend holding USB handles that died with it, keeps accepting SDK packets (so every
    /// liveness probe still answers "fine") and silently stops driving the hardware. That is the
    /// state the user could only clear by closing and reopening the app.
    ///
    /// THREE triggers, because the obvious one is not reliable:
    ///  1. A REAL window of ours, registered for <c>WM_POWERBROADCAST</c> and
    ///     <c>WM_WTSSESSION_CHANGE</c> (<see cref="Setup.PowerMonitor"/>) — the managed
    ///     <c>SystemEvents.PowerModeChanged</c> event needs a window the runtime never registered
    ///     and was reproduced as NOT firing at all on Windows 11 (dotnet/runtime#78162), and Modern
    ///     Standby sends nothing either. The session notifications keep working when no power
    ///     broadcast arrives at all (lock/unlock, console connect, logon).
    ///  2. A heartbeat, which needs no OS notification: a DispatcherTimer cannot tick while the
    ///     machine is suspended, so a gap several times its own interval can only mean it slept.
    ///     <c>Environment.TickCount64</c> counts sleep and hibernate time (unlike
    ///     QueryUnbiasedInterruptTime), and the wall clock is checked too.
    ///  3. <see cref="Setup.LightingWatchdog"/>, which does not need to know that anything happened
    ///     at all: once a minute it checks from the SIDE whether the engine is still applying
    ///     frames, and rebuilds the session if it is not. This is what covers a suspend that none
    ///     of the notifications describe — including a power cut.
    /// </summary>
    private void HookPowerEvents()
    {
        if (_powerHooked) return;
        _powerHooked = true;
        try { SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch { }

        _sessionIdAtStart = Setup.PowerMonitor.CurrentSessionId();
        try
        {
            _powerMonitor = new Setup.PowerMonitor();
            _powerMonitor.Suspended += () =>
            {
                // Settings half of "the last settings don't come up after boot": flush pending
                // effect edits before the freeze.
                try { FlushPendingEdits(); } catch { }
            };
            _powerMonitor.Resumed += () => BeginResumeRecovery();
            _powerMonitor.SessionChanged += OnSessionChanged;
            _powerMonitor.SessionEnding += () => { _osShuttingDown = true; };
        }
        catch { _powerMonitor = null; }

        _lastHeartbeat = Environment.TickCount64;
        _lastWallClock = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        _resumeHeartbeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(HeartbeatSeconds),
        };
        _resumeHeartbeat.Tick += (_, _) =>
        {
            long tick = Environment.TickCount64;
            long wall = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            long tickGap = tick - _lastHeartbeat;
            long wallGap = wall - _lastWallClock;
            _lastHeartbeat = tick;
            _lastWallClock = wall;
            if (IsResumeGap(tickGap, wallGap, (long)_resumeHeartbeat.Interval.TotalMilliseconds))
                BeginResumeRecovery();
        };
        _resumeHeartbeat.Start();
    }

    /// <summary>
    /// The independent watchdog: it does not need to be told that the machine slept. Every minute
    /// it samples the engine from the side (see <see cref="Setup.LightingWatchdog"/>) and acts when
    /// frames have stopped reaching the hardware, which is the state the user could only clear by
    /// hand. A <c>Repair</c> verdict takes the same path as a wake-up; a <c>Rebuild</c> verdict is
    /// the cheap version (no process restart) used when the engine simply kept zones at 0 LEDs.
    /// </summary>
    private void StartLightingWatchdog()
    {
        if (_lightingWatchdog is not null) return;
        _lightingWatchdog = new Setup.LightingWatchdog(
            TimeSpan.FromSeconds(60),
            () => new Setup.LightingWatchdog.Stats
            {
                Port = App.Settings.ServerPort,
                EngineRunning = _client is { Connected: true },
                EffectsRunning = _engine?.IsRunning == true,
                SecondsSinceSession = SecondsSinceSession,
                AnimatedDeviceNames = AnimatedDeviceNameSet,
            },
            (verdict, snap) => Dispatcher.BeginInvoke(() =>
            {
                try { _ = OnWatchdogVerdictAsync(verdict, snap); } catch { }
            }));
        // A probe that finds frames moving again (after a repair, or after the user pressed Rescan)
        // clears the "recovering" status instead of leaving the warning on screen.
        _lightingWatchdog.MotionDetected += () => Dispatcher.BeginInvoke(() =>
        {
            if (_statusIsRecovering && _engine?.IsRunning == true)
            {
                _statusIsRecovering = false;
                SetStatus(L10n.T("status.recovered"), StatusKind.Ok);
            }
        });
        _lightingWatchdog.Start();
    }

    /// <summary>True while the last status line was one of the recovery messages.</summary>
    private bool _statusIsRecovering;

    /// <summary>Rebuild attempts that produced no new LEDs; after two the engine is replaced.</summary>
    private int _rebuildsWithoutGrowth;

    private async Task OnWatchdogVerdictAsync(Setup.EngineShadow.Verdict verdict, Setup.EngineShadow.Snapshot snap)
    {
        if (_reallyExiting || _osShuttingDown || !IsLoaded) return;
        bool owns = _resuming || _rescanning;          // a repair already owns the session
        if (!App.Settings.AutoRecoverLighting)
        {
            // The user turned the automatic fix off: still say what is wrong, change nothing.
            if (!owns && !_statusIsRecovering)
            {
                _statusIsRecovering = true;
                SetStatus(L10n.T("status.recover", L10n.T("status.recoverFrozen")), StatusKind.Warn);
            }
            return;
        }
        if (owns) return;
        if (verdict == Setup.EngineShadow.Verdict.Rebuild && _rebuildsWithoutGrowth < 2)
        {
            _rebuildsWithoutGrowth++;
            _statusIsRecovering = true;
            SetStatus(L10n.T("status.recover", snap.LedTotal == 0
                ? L10n.T("status.recoverZones") : L10n.T("status.recoverFrozen")), StatusKind.Warn);
            await RebuildZonesAsync();
            return;
        }
        // Either the engine is gone, or re-expanding twice changed nothing: replace the engine
        // exactly the way a wake-up does (teardown, restart, reconnect, re-apply).
        _rebuildsWithoutGrowth = 0;
        _statusIsRecovering = true;
        SetStatus(L10n.T("status.recover", L10n.T("status.recoverEngine")), StatusKind.Warn);
        BeginResumeRecovery();
    }

    /// <summary>
    /// The cheap recovery: re-expand the addressable zones, re-assert Direct mode, forget the
    /// "already painted this" cache and re-apply. No engine restart, so a session that merely came
    /// up before USB was ready is repaired in a second instead of a process cycle.
    /// </summary>
    private async Task RebuildZonesAsync()
    {
        var client = _client;
        var engine = _engine;
        if (client is null || engine is null) return;
        var profile = CurrentProfile();
        try
        {
            await Task.Run(() =>
            {
                try { client.ExpandAllZones(default, (d, z) => profile.ZoneSize(d, z)); } catch { }
                try { client.EnsureDirectMode(); } catch { }
            });
            // The dedupe cache would suppress a frame that only LOOKS unchanged, and a re-opened
            // session can hold stale colours on the device: force the next frames out.
            engine.InvalidateFrames();
            if (engine.IsRunning) engine.Apply(profile);
            _sessionStartedUtc = DateTime.UtcNow;
            RefreshStatus();
            SyncRunButtons();
        }
        catch (Exception e)
        {
            SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error);
        }
    }

    /// <summary>
    /// Writes any pending effect edit to disk right now. The editor saves on a 1.5 s debounce and a
    /// suspend can cut that short, leaving the user's last pick only in memory — the other half of
    /// "the last settings don't come up after boot". Called on suspend and on the lock before one.
    /// </summary>
    private void FlushPendingEdits()
    {
        try { PushEdit(); } catch { }
        try { ProfileStore.Save(App.Settings); } catch { }
    }

    /// <summary>
    /// Names of the devices whose effect changes every frame. Used by the watchdog to decide
    /// whether "the stored colours did not move" is a stall or simply a static colouring the user
    /// asked for (Solid/Gradient must never trigger a repair).
    /// </summary>
    private ISet<string> AnimatedDeviceNameSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var profile = CurrentProfile();
            foreach (var dev in _client?.Controllers ?? Enumerable.Empty<SDK.RgbController>())
            {
                bool animated = false;
                foreach (var zone in dev.Zones.Where(z => z.LedsCount > 0))
                    if (profile.EffectFor(dev, zone).IsAnimated) { animated = true; break; }
                if (animated) set.Add(dev.Name);
            }
        }
        catch { }
        return set;
    }

    /// <summary>
    /// Session transitions that reach us even when no power broadcast does. Two cases matter:
    ///
    ///  * UNLOCK / CONSOLE CONNECT — the machine is being used again. If it also slept (the
    ///    heartbeat says so) the wake-up repair runs; if it did not, nothing is done, because a
    ///    lock/unlock on its own does not invalidate a HID connection (and the watchdog still
    ///    watches the frames).
    ///  * A DIFFERENT terminal session than the one we started in — i.e. the user logged on again.
    ///    A stale engine from the previous session is then the likely reason for a dark machine,
    ///    and it is exactly the case the app's own client cannot see: it is still "connected".
    ///    Identity is the terminal-session id (<c>ProcessIdToSessionId</c>), not a number typed by
    ///    hand, so the same interactive session never triggers this.
    /// </summary>
    private void OnSessionChanged(Setup.SessionEvent ev)
    {
        bool machineSlept = _resumeHeartbeat is not null
            && IsResumeGap(Math.Max(0, Environment.TickCount64 - _lastHeartbeat),
                           Math.Max(0, DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - _lastWallClock),
                           (long)_resumeHeartbeat.Interval.TotalMilliseconds);
        if (machineSlept)
        {
            BeginResumeRecovery();
            return;
        }
        // A LOCK is the last moment before a suspend in most workflows (lock screen, then sleep):
        // flush the pending effect edit so a hibernate cannot lose it.
        if (ev is Setup.SessionEvent.Lock or Setup.SessionEvent.Logoff or Setup.SessionEvent.ConsoleDisconnect)
        {
            try { FlushPendingEdits(); } catch { }
            return;
        }
        if (ev is not (Setup.SessionEvent.Unlock or Setup.SessionEvent.ConsoleConnect)) return;
        int nowSession = Setup.PowerMonitor.CurrentSessionId();
        if (nowSession < 0 || _sessionIdAtStart < 0 || nowSession == _sessionIdAtStart) return;
        // We are in a different terminal session than the one we started in: treat it like a wake.
        BeginResumeRecovery();
    }

    /// <summary>
    /// True when a heartbeat arrived so late that the process must have been frozen in between.
    /// <see cref="ResumeGapFactor"/> × the interval (20 s at the 5 s heartbeat) is far beyond any
    /// normal dispatcher delay, so a busy UI thread is never mistaken for a resume.
    /// </summary>
    internal static bool IsResumeGap(long tickGapMs, long wallGapMs, long intervalMs)
        => intervalMs > 0 && Math.Max(tickGapMs, wallGapMs) > intervalMs * ResumeGapFactor;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Settings half of "the last settings don't come up after boot": flush pending effect
        // edits before the freeze. The event itself is unreliable (the heartbeat is the real
        // detector), so this is free insurance, not the primary save path.
        if (e.Mode == PowerModes.Suspend)
        {
            try { ProfileStore.Save(App.Settings); } catch { /* the edit debounce and OnExit still cover it */ }
            return;
        }
        if (e.Mode != PowerModes.Resume) return;
        BeginResumeRecovery();
    }

    /// <summary>Marshals the repair onto the UI thread: both triggers can arrive on a foreign thread.</summary>
    private void BeginResumeRecovery()
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(async () => { try { await RecoverAfterResumeAsync(); } catch { } });
        }
        catch { }
    }

    /// <summary>
    /// Rebuilds the whole lighting path after a suspend, retried because the first attempt can
    /// race USB re-enumeration (and the engine's own start-up).
    /// </summary>
    private async Task RecoverAfterResumeAsync()
    {
        if (_reallyExiting || _osShuttingDown || !IsLoaded) return;
        // A second suspend can land in the middle of a repair: remember it instead of dropping it,
        // otherwise that wake-up would never be repaired at all.
        if (_resuming) { _resumePending = true; return; }
        _resuming = true;
        try
        {
            // Capture BEFORE the first teardown: once an attempt has replaced the engine, "was it
            // running" is no longer readable from the live state, so later attempts would silently
            // skip Apply and the user's effect would never come back after wake.
            bool wantPaint = _engine?.IsRunning == true || App.Settings.AutoStartEffects;

            // The user often clicks Rescan the moment the screen comes on: let an in-flight manual
            // rescan finish instead of disposing the SDK session under it. Clicks that land DURING
            // the repair are queued by RescanAsync and drained in the finally below.
            for (int i = 0; i < 40 && _rescanning; i++)
                await Task.Delay(TimeSpan.FromMilliseconds(250));

            // USB/SMBus re-enumeration takes a few seconds after resume; touching the engine before
            // that finds no devices and would look like the bug we are fixing.
            await Task.Delay(TimeSpan.FromSeconds(3));

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (_reallyExiting || _osShuttingDown || !IsLoaded) return;
                SetStatus(L10n.T("status.resume"), StatusKind.Busy);
                if (await TryRestoreLightingAsync(wantPaint)) return;
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
            SetStatus(L10n.T("status.resumeFailed"), StatusKind.Error);
        }
        finally
        {
            _resuming = false;
            if (_resumePending) { _resumePending = false; BeginResumeRecovery(); }
            else if (_rescanQueued) { _rescanQueued = false; _ = RescanAsync(); }
            // Even a SUCCESSFUL repair can end on a short device list (some hardware enumerates
            // even later). OnConnected skips scheduling while _resuming, so ask from here too.
            ScheduleAutoRescanIfNeeded();
        }
    }

    /// <summary>
    /// One repair attempt: tear the stale session down, replace the engine process, reconnect and
    /// re-apply. A plain rescan is NOT enough here — see <see cref="HookPowerEvents"/> — so the
    /// engine is restarted exactly the way closing and reopening the app restarted it.
    /// <paramref name="wantPaint"/> is captured by the caller before any teardown — see
    /// <see cref="RecoverAfterResumeAsync"/>.
    /// </summary>
    private async Task<bool> TryRestoreLightingAsync(bool wantPaint)
    {
        // 1) Drop the dead session. Everything below is rebuilt from scratch: the per-device
        //    sockets, the engine and the WASAPI capture, whose audio session died with the suspend.
        //    The fields are cleared FIRST so no UI code can touch the corpses, and the teardown
        //    itself runs off the UI thread (stopping the loops joins their writer threads).
        var engine = _engine;
        var client = _client;
        var audio = _audio;
        _engine = null;
        _client = null;
        _audio = null;
        await Task.Run(() =>
        {
            try { engine?.Dispose(); } catch { }   // Dispose() stops the loops and their writers
            try { client?.Dispose(); } catch { }
            try { audio?.Dispose(); } catch { }
        });

        // 2) Replace the engine process. An engine started by the elevated task survives a plain
        //    Stop(), so that instance is ended through the task first (no UAC); RestartAsync then
        //    kills leftovers, waits for the port to close and starts a fresh engine, so a wedged
        //    post-suspend server can never be re-attached. Off the UI thread too: `schtasks` is
        //    synchronous and can block for seconds.
        try
        {
            _mgr ??= new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath(), App.Settings.ServerPort);
            var mgr = _mgr;
            await Task.Run(async () =>
            {
                if (mgr.AttachedToExisting) Setup.EngineTask.EndTaskInstance();
                await mgr.RestartAsync();
            });
        }
        catch { /* a failed attempt is retried by the caller */ }

        if (_reallyExiting || _osShuttingDown) return false;

        // 3) Reconnect: ConnectAsync → OnConnected → StartEngine rebuilds the engine and the panels.
        //    ConnectAsync first waits for the device list to settle: the restarted engine opens its
        //    SDK port BEFORE its USB detection finishes, and "connected" alone used to be reported
        //    as repaired while an empty device list meant nothing was ever painted — the exact
        //    "dark until I press Rescan" symptom.
        await ConnectAsync();
        _sessionStartedUtc = DateTime.UtcNow;
        if (_client is not { Connected: true } || _engine is null) return false;

        if (wantPaint)
        {
            _engine.Apply(CurrentProfile());
            // Success means the per-device loops are actually RUNNING, i.e. there was something
            // to paint. Connected-but-empty must fail so the caller retries, and the last attempt
            // falls back to status.resumeFailed instead of a false "restored".
            if (!_engine.IsRunning) return false;
        }
        RefreshStatus();
        SyncRunButtons();
        SetStatus(L10n.T("status.resumed"), StatusKind.Ok);
        return true;
    }

    // ---------- connection ----------

    private void OnConnected()
    {
        var profile = CurrentProfile();
        // Drop overrides for hardware that is no longer here, so settings.json cannot grow forever.
        // The remembered inventory is passed too: a device that is merely missing from THIS scan
        // (cold boot, slow USB) keeps its overrides instead of losing them silently.
        if (_client is not null)
        {
            profile.PruneTo(_client.Controllers, App.DeviceCache?.KnownKeys());
            App.RememberDevices(_client.Controllers);
        }

        StartEngine();
        _sessionStartedUtc = DateTime.UtcNow;
        _rebuildsWithoutGrowth = 0;
        _statusIsRecovering = false;
        _edit = EffectEngine.Clone(profile.GlobalEffect);
        BuildDeviceList();
        BuildDevicePicker();
        BuildEffectEditor();
        LoadProfileToUi();
        RefreshStatus();
        CheckIcue();
        CheckSmbus();
        // Boot and wake can both hand over a SHORT device list (USB enumeration still running,
        // or the splash accepting a stable-but-partial answer). Painting what showed up is right;
        // the missing hardware gets ONE automatic rescan instead of waiting for the user to notice
        // and press Rescan by hand.
        ScheduleAutoRescanIfNeeded();
    }

    private void RefreshStatus()
    {
        if (_client is null) return;
        int leds = _client.Controllers.Sum(c => c.LedCount);
        // The pill and the subtitle must NOT say the same thing: the pill is the live state,
        // the subtitle is the inventory. Duplicating one sentence in both places also made both
        // of them truncate.
        _inventory = L10n.T("dev.inventory", _client.Controllers.Count, leds);
        SubTitleTxt.Text = PageSubtitle();
        SetStatus(L10n.T("status.ready"), StatusKind.Ok);
        _tray?.SetTooltip(L10n.T("tray.tip", _inventory));
        UpdateDiagnostics();
        UpdateEngineCard();
        UpdateHeroCaption();
        RefreshStats();
    }

    /// <summary>The heading subtitle: which page the user is on. Never the inventory sentence.</summary>
    private string PageSubtitle()
    {
        string page = TabLighting.IsChecked == true ? "lighting"
            : TabDevices.IsChecked == true ? "devices"
            : TabHardware.IsChecked == true ? "hardware"
            : "settings";
        return L10n.T("page.sub." + page);
    }

    /// <summary>
    /// The sidebar engine card is on every page, so it is fed from here rather than from the
    /// Settings-only diagnostics line — otherwise it kept saying "not connected" after the
    /// engine had come up.
    /// </summary>
    private void UpdateEngineCard()
    {
        if (EngineStateTxt is null) return;
        bool connected = _client is { Connected: true };
        EngineStateTxt.Text = L10n.T(connected ? "diag.connected" : "diag.offline");
        if (FindName("EngineStateDot") is System.Windows.Shapes.Ellipse dot)
            dot.Fill = (Brush)FindResource(connected ? "Ok" : "Faint");
        if (EngineStatsTxt is not null)
            EngineStatsTxt.Text = !connected ? ""
                : L10n.T("engine.stats", _client!.ProtocolVersion,
                         _engine is { IsRunning: true } ? $"{_engine.Fps:0} FPS" : L10n.T("status.stopped"));
    }

    private void UpdateDiagnostics()
    {
        if (DiagTxt is null) return;
        // Never show placeholder junk like "Engine: - · protocol v0 · 0 fps": until the SDK is
        // connected the line says so in words, and fps only appears while the engine runs.
        if (_client is null || !_client.Connected)
        {
            DiagTxt.Text = L10n.T("diag.offline");
            return;
        }
        string engine = _mgr is null ? L10n.T("diag.unknown")
            : (_mgr.AttachedToExisting ? L10n.T("diag.attached") : L10n.T("diag.own"));
        var parts = new List<string>
        {
            L10n.T("diag.engine", engine),
            L10n.T("diag.protocol", _client.ProtocolVersion),
        };
        double fps = _engine?.Fps ?? 0;
        if (_engine?.IsRunning == true && fps >= 1)
        {
            // Render rate and delivered rate differ on devices that cannot keep up (measured:
            // the Corsair Commander Core applies ~3 frames/s). Showing both is honest; showing
            // only "30 fps" would be a lie about what the hardware receives.
            double delivered = _engine.DeliveredFps;
            parts.Add(delivered >= 1 && delivered < fps - 1.5
                ? L10n.T("status.rate2", fps.ToString("0"), delivered.ToString("0"))
                : L10n.T("status.rate", fps.ToString("0")));
        }
        else if (_engine?.IsRunning != true) parts.Add(L10n.T("status.stopped"));
        DiagTxt.Text = string.Join(" · ", parts);
    }

    private async Task ConnectAsync()
    {
        SetStatus(L10n.T("status.starting"), StatusKind.Busy);
        try
        {
            _mgr ??= new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath(), App.Settings.ServerPort);
            await _mgr.StartAsync();
            _client ??= new OpenRgbClient();
            await Task.Run(() => _client!.Connect("127.0.0.1", App.Settings.ServerPort, "FullRGB"));

            // The engine opens its SDK port BEFORE its USB detection finishes (the splash guards
            // the same race with a stability loop). The post-wake repair connects at exactly that
            // instant — right after restarting the engine — so the first answer is often a short
            // or empty device list: nothing to expand, nothing to paint, no retry, and only a
            // manual Rescan ever brought the lights back. Wait here for the list to settle; late
            // devices are then expanded and painted by the code below. Bounded: an empty session
            // pays the full window, a stable answer exits in ~2 s.
            int remembered = App.DeviceCache?.Devices.Count ?? 0;
            int last = -1, stable = 0;
            for (int i = 0; i < 10; i++)
            {
                int now = _client!.Controllers.Count;
                if (now == 0) stable = 0;
                else if (now == last) stable++;
                else stable = 0;
                last = now;
                if (now > 0 && stable >= 2 && (i >= 4 || now >= remembered)) break;
                if (i == 9) break;
                try
                {
                    await Task.Delay(800);
                    await Task.Run(() => _client.RefreshControllers());
                }
                catch { break; } // session died mid-wait: the caller's Connected check reports it
            }

            SetStatus(L10n.T("scan.step.zones"), StatusKind.Busy);
            var profile = CurrentProfile();
            await Task.Run(() => _client!.ExpandAllZones(default, (d, z) => profile.ZoneSize(d, z)));
            OnConnected();
        }
        catch (Exception e)
        {
            SetStatus(L10n.T("status.failed", e.Message), StatusKind.Error);
        }
    }

    private void StartEngine()
    {
        if (_client is null || _engine is not null) return;
        try { _temps.Start(); } catch { }
        // Rebuilt engines must not leave the previous capture (and its screen sampler) running:
        // StartEngine also runs again after a suspend, when the audio session died with it.
        try { _audio?.Dispose(); } catch { }
        try { _audio = new AudioProvider(); _audio.Start(); }
        catch { _audio = null; _audioFailed = true; }

        _engine = new EffectEngine(_client, _temps, _audio);
        _engineStatusHandler = m =>
        {
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(() => { try { SetStatus(m, StatusKind.Warn); } catch { } });
            }
            catch { }
        };
        _engine.Status += _engineStatusHandler;

        // If OpenRGB itself dies OR wedges (accepts TCP, never answers), restart the process
        // and rebuild the session. RestartAsync → StartAsync now SDK-probes the old server and
        // force-kills a corpse, including the task engine via `schtasks /End` (no UAC).
        _engine.ReviveEngine = ct =>
        {
            try
            {
                _mgr ??= new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath(), App.Settings.ServerPort);
                _mgr.RestartAsync().GetAwaiter().GetResult();
                Thread.Sleep(3500); // detection settle
                _client!.Connect("127.0.0.1", App.Settings.ServerPort, "FullRGB", ct);
                var p = CurrentProfile();
                _client.ExpandAllZones(ct, (d, z) => p.ZoneSize(d, z));
                _client.EnsureDirectMode();
                Dispatcher.BeginInvoke(() => { BuildDeviceList(); BuildDevicePicker(); RefreshStatus(); });
                return true;
            }
            catch { return false; }
        };

        // Watchdog for the SILENT wedge: the engine keeps every TCP connection open but stops
        // replying, so no write fails and ReviveEngine is never called — the GUI just sits there
        // painting into a corpse (the "must kill OpenRGB from Task Manager" bug). Once a minute,
        // do a cheap one-packet health probe; on two consecutive failures, revive.
        // Built ONCE: StartEngine runs again whenever the engine is rebuilt (post-suspend repair),
        // and a second watchdog would double every probe and every revive.
        if (_engineWatchdog is null)
        {
            _engineWatchdog = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1),
            };
            int wdFails = 0;
            _engineWatchdog.Tick += (_, _) =>
            {
                try
                {
                    if (_client is null || !_client.Connected || _engine is { IsRunning: false }) return;
                    bool alive = _mgr?.SdkAliveAsync().GetAwaiter().GetResult() ?? true;
                    wdFails = alive ? 0 : wdFails + 1;
                    if (wdFails >= 2)
                    {
                        wdFails = 0;
                        SetStatus(L10n.T("status.engineRevive"), StatusKind.Busy);
                        Task.Run(() =>
                        {
                            try { _engine?.ReviveEngine?.Invoke(CancellationToken.None); }
                            catch { }
                            Dispatcher.BeginInvoke(() => { try { RefreshStatus(); } catch { } });
                        });
                    }
                }
                catch { }
            };
            _engineWatchdog.Start();
        }

        StartPreview();
        if (App.Settings.AutoStartEffects) _engine.Apply(CurrentProfile());
        UpdateEngineCard();
        SyncRunButtons();
    }

    private Profile CurrentProfile() =>
        App.Settings.Profiles.FirstOrDefault(x => x.Name == App.Settings.ActiveProfile)
        ?? App.Settings.Profiles.FirstOrDefault()
        ?? new Profile();

    // ---------- banners ----------

    private void CheckIcue()
    {
        bool running = Process.GetProcessesByName("iCUE").Length > 0;
        IcueBanner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) IcueTxt.Text = L10n.T("icue.text");
    }

    private void CheckSmbus()
    {
        // RAM/GPU need SMBus; the engine log tells us whether PawnIO came up.
        bool failed = _mgr?.LastRunHadSmbusFailure() ?? false;
        bool haveDram = _client?.Controllers.Any(c => c.Kind == RgbDeviceType.DRAM) ?? false;
        SmbusBanner.Visibility = failed && !haveDram ? Visibility.Visible : Visibility.Collapsed;
        SmbusTxt.Text = L10n.T("smbus.warn");
    }

    private void CloseIcue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Process.GetProcessesByName("iCUE"))
            try { p.Kill(); } catch { }
        foreach (var svc in new[] { "iCUEHIDService", "CorsairService", "CorsairGamingAudioConfig" })
        {
            try
            {
                Process.Start(new ProcessStartInfo("net", $"stop \"{svc}\"")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }
        CheckIcue();
    }

    // ---------- window chrome ----------

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void CloseWin_Click(object sender, RoutedEventArgs e) => Close();

    private void StatusPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Rescan_Click(sender, e);

    // ---------- language / tabs ----------

    private void LangBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Language = App.Settings.Language == "en" ? "fa" : "en";
        L10n.Set(App.Settings.Language);
        ProfileStore.Save(App.Settings);
        ApplyLanguage();
        BuildDeviceList();
        BuildDevicePicker();
        BuildEffectEditor();
        LoadProfileToUi();
        CheckIcue();
        CheckSmbus();
        RefreshStatus();
        _tray?.ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        bool fa = L10n.IsRtl;
        // Physical shell (sidebar always right) stays LTR; language mirroring is applied
        // to the content column and the sidebar interior only.
        var fd = fa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (MainCol is not null) MainCol.FlowDirection = fd;
        if (SidebarFlow is not null) SidebarFlow.FlowDirection = fd;
        if (TopbarFlow is not null) TopbarFlow.FlowDirection = fd;
        if (ActionBarFlow is not null) ActionBarFlow.FlowDirection = fd;
        LangBtn.Content = fa ? "EN" : "فا";
        StatusPill.ToolTip = L10n.T("btn.rescan");
        TabLighting.Content = L10n.T("tab.lighting");
        TabDevices.Content = L10n.T("tab.devices");
        TabHardware.Content = L10n.T("tab.hardware");
        TabSettings.Content = L10n.T("tab.settings");
        UpdatePageHeading();
        if (EngineTitleTxt is not null) EngineTitleTxt.Text = L10n.T("engine.cardTitle");
        if (EngineStateTxt is not null) EngineStateTxt.Text = L10n.T("diag.offline");
        if (VersionTxt is not null) VersionTxt.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.0");
        if (HeroLiveTxt is not null) HeroLiveTxt.Text = L10n.T("hero.live");
        if (HeroL1Txt is not null) HeroL1Txt.Text = L10n.T("hero.l1");
        if (HeroL2Txt is not null) HeroL2Txt.Text = L10n.T("hero.l2");
        if (HeroCornerTxt is not null) HeroCornerTxt.Text = L10n.T("hero.corner");
        if (StatDevLbl is not null) StatDevLbl.Text = L10n.T("stats.devices");
        if (StatLedLbl is not null) StatLedLbl.Text = L10n.T("stats.leds");
        if (StatFxLbl is not null) StatFxLbl.Text = L10n.T("stats.effect");
        if (StatSyncLbl is not null) StatSyncLbl.Text = L10n.T("stats.sync");
        if (ControlsHdr is not null) ControlsHdr.Text = L10n.T("controls.title");
        if (ConnHdr is not null) ConnHdr.Text = L10n.T("connected.title");
        if (ConnManageBtn is not null) ConnManageBtn.Content = L10n.T("connected.manage") + "  ›";
        if (FxHintTxt is not null) FxHintTxt.Text = L10n.T("effects.hint");
        if (FxFooterTxt is not null) FxFooterTxt.Text = L10n.T("effects.footer");
        if (FxCountTxt is not null) FxCountTxt.Text = L10n.T("effects.count", 18);
        if (SaveProfileBtn2 is not null) SaveProfileBtn2.Content = L10n.T("btn.saveToDevices");
        if (DevRescanBtn is not null) DevRescanBtn.Content = L10n.T("btn.rescan");
        if (DevRescanHint is not null) DevRescanHint.Text = L10n.T("dev.rescanHint");
        if (ProfileQuickBtn is not null) ProfileQuickBtn.Content = CurrentProfile().Name;
        if (PowerBtn is not null) PowerBtn.ToolTip = L10n.T("power.on");
        UpdateEngineCard();
        IcueTxt.Text = L10n.T("icue.text");
        CloseIcueBtn.Content = L10n.T("icue.close");
        SmbusTxt.Text = L10n.T("smbus.warn");
        BlackoutBtn.Content = L10n.T("btn.blackout");
        SaveBtn.Content = L10n.T("btn.saveToDevices");
        ProfileHdr.Text = L10n.T("profile");
        ProfileRenameBtn.ToolTip = L10n.T("dlg.rename");
        ProfileNewBtn.ToolTip = L10n.T("profile.new");
        ProfileDelBtn.ToolTip = L10n.T("profile.delete");
        AppearanceHdr.Text = L10n.T("settings.appearance");
        AccentLbl.Text = L10n.T("settings.accent");
        StartupHdr.Text = L10n.T("settings.startup");
        AutostartChk.Content = L10n.T("settings.autostart");
        MinimizedChk.Content = L10n.T("settings.minimized");
        AutoFxChk.Content = L10n.T("settings.autofx");
        CloseEngineChk.Content = L10n.T("settings.closeEngine");
        CloseEngineChk.ToolTip = L10n.T("settings.closeEngineHint");
        if (AutoRecoverChk is not null)
        {
            AutoRecoverChk.Content = L10n.T("settings.autorecover");
            AutoRecoverChk.ToolTip = L10n.T("settings.autorecoverHint");
        }
        SchedHdr.Text = L10n.T("sched.title");
        SchedChk.Content = L10n.T("sched.enable");
        SchedEveryLbl.Text = L10n.T("sched.every");
        FgHdr.Text = L10n.T("fg.title");
        FgChk.Content = L10n.T("fg.enable");
        FgHint.Text = L10n.T("fg.hint");
        BackupHdr.Text = L10n.T("backup.title");
        ExportBtn.Content = L10n.T("backup.export");
        ImportBtn.Content = L10n.T("backup.import");
        AboutHdr.Text = L10n.T("about.title");
        AboutTxt.Text = L10n.T("about.body");
        // The hardware page owns AdvancedHdr / Why*Txt now; BuildHardwarePage fills them when the
        // page is opened, but set them here too so a language switch is applied even if the user
        // never visits the page.
        AdvancedHdr.Text = L10n.T("hw.setupTitle");
        WhyHdr.Text = L10n.T("hw.whyTitle");
        Why1Txt.Text = L10n.T("hw.why1");
        Why2Txt.Text = L10n.T("hw.why2");
        Why3Txt.Text = L10n.T("hw.why3");
        RefreshAdvanced();
        DevPickHdr.Text = L10n.T("dev.pickTitle");
        HelpExp.Header = L10n.T("missing.title");
        MissingTxt.Text = L10n.T("missing.body");
        SyncRunButtons();
        UpdateDiagnostics();
        Title = "FullRGB";
    }

    /// <summary>Page heading + breadcrumb follow the selected nav tab.</summary>
    private void UpdatePageHeading()
    {
        if (PageTitleTxt is null) return;
        string page = TabLighting.IsChecked == true ? "lighting"
            : TabDevices.IsChecked == true ? "devices"
            : TabHardware.IsChecked == true ? "hardware"
            : "settings";
        PageTitleTxt.Text = L10n.T("page.h1." + page);
        if (PageCrumb is not null) PageCrumb.Text = L10n.T("tab." + page);
        if (PageEyebrow is not null) PageEyebrow.Text = L10n.T("page.eyebrow." + page);
        if (SubTitleTxt is not null) SubTitleTxt.Text = L10n.T("page.sub." + page);
    }

    /// <summary>Quick profile picker in the page heading (arena profile-select).</summary>
    private void ProfileQuick_Click(object sender, RoutedEventArgs e)
    {
        try { SwitchProfile(NextProfileName()); } catch { }
    }

    private string NextProfileName()
    {
        var names = App.Settings.Profiles.Select(p => p.Name).ToList();
        if (names.Count == 0) return CurrentProfile().Name;
        int i = names.FindIndex(n => n.Equals(CurrentProfile().Name, StringComparison.OrdinalIgnoreCase));
        return names[(i + 1) % names.Count];
    }

    /// <summary>Power button: start/stop the lighting engine.</summary>
    private void Power_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_engine?.IsRunning == true)
            {
                _engine.Stop();
                SetStatus(L10n.T("status.stopped"), StatusKind.Info);
            }
            else
            {
                _engine?.Apply(CurrentProfile());
                RefreshStatus();
            }
            SyncPowerButton();
            SyncRunButtons();
            UpdateHeroCaption();
            RefreshStats();
        }
        catch { }
    }

    /// <summary>
    /// Reflects the engine state on the heading power button: the filled state the template
    /// keys off <c>Tag</c>, and the tooltip that names the action it will perform next.
    /// </summary>
    private void SyncPowerButton()
    {
        if (PowerBtn is null) return;
        bool running = _engine?.IsRunning == true;
        PowerBtn.Tag = running ? "on" : null;
        PowerBtn.ToolTip = L10n.T(running ? "power.off" : "power.on");
    }

    /// <summary>Jump to the Devices tab from the connected-devices panel.</summary>
    private void GotoDevices_Click(object sender, RoutedEventArgs e)
    {
        TabDevices.IsChecked = true;
    }

    /// <summary>Hero + stats strip that mirror the arena lighting page.</summary>
    private void RefreshStats()
    {
        if (StatDevVal is null) return;
        int devices = _client?.Controllers.Count ?? 0;
        int leds = _client?.Controllers.Sum(c => c.LedCount) ?? 0;
        StatDevVal.Text = devices.ToString();
        StatDevUnit.Text = L10n.T("tab.devices");
        StatLedVal.Text = leds.ToString();
        var entry = typeof(MainWindow).GetField("_edit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        StatFxVal.Text = HeroFxTxt?.Text ?? "";
        bool sync = _edit.SyncZones;
        StatSyncVal.Text = L10n.T(sync ? "stats.syncOn" : "stats.syncOff");
        StatSyncDot.Fill = (Brush)FindResource(sync ? "Ok" : "Faint");
        if (HeroL1Txt is not null) HeroL1Txt.Text = L10n.T("hero.l1");
        if (HeroL2Txt is not null) HeroL2Txt.Text = L10n.T("hero.l2");
        if (HeroLiveTxt is not null) HeroLiveTxt.Text = L10n.T("hero.live");
        if (HeroCornerTxt is not null) HeroCornerTxt.Text = L10n.T("hero.corner");
        if (HeroTagDevTxt is not null)
            HeroTagDevTxt.Text = $"{devices} {L10n.T("tab.devices")}";
        if (HeroTagFxTxt is not null)
            HeroTagFxTxt.Text = HeroFxTxt?.Text ?? "";
        RebuildConnectedGrid();
    }

    private void RebuildConnectedGrid()
    {
        if (ConnectedGrid is null) return;
        ConnectedGrid.Children.Clear();
        var ctrls = _client?.Controllers ?? Enumerable.Empty<SDK.RgbController>();
        int n = 0;
        foreach (var c in ctrls)
        {
            n++;
            var cell = new Border
            {
                Background = (Brush)FindResource("Surface"),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0xA4, 0x87, 0xEF)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xA4, 0x87, 0xEF)),
                BorderThickness = new Thickness(1),
            };
            icon.Child = new TextBlock
            {
                Text = "\uE977",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA4, 0x87, 0xEF)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = ShortName(c.Name),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("Text"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = c.LedCount + " LED",
                FontSize = 9,
                Foreground = (Brush)FindResource("Muted"),
                Margin = new Thickness(0, 3, 0, 0),
            });
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = (Brush)FindResource("Ok"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 2);
            row.Children.Add(dot);
            cell.Child = row;
            ConnectedGrid.Children.Add(cell);
        }
        if (ConnCountTxt is not null) ConnCountTxt.Text = n.ToString();
    }

    /// <summary>Switches the visible page when a nav item is picked.</summary>
    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        // fires mid-InitializeComponent (IsChecked=true on the first nav item) before the
        // later pages / nav siblings / heading fields have been assigned.
        if (PageLighting is null || PageDevices is null || PageHardware is null || PageSettings is null
            || TabDevices is null || TabHardware is null || TabSettings is null)
            return;
        PageLighting.Visibility = TabLighting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = TabDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHardware.Visibility = TabHardware.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageHeading();
        if (TabSettings.IsChecked == true) UpdateDiagnostics();
        // Rebuilt on entry, not cached: devices come and go, and a USB scan costs ~15 ms.
        if (TabHardware.IsChecked == true) BuildHardwarePage();
    }

    // Screenshot hooks (--uishot): switch pages without a mouse.
    internal void ShowDevicesTabForShot() { TabDevices.IsChecked = true; UpdateLayout(); }
    internal void ShowHardwareTabForShot() { TabHardware.IsChecked = true; UpdateLayout(); }
    internal void ShowSettingsTabForShot() { TabSettings.IsChecked = true; UpdateLayout(); }

    // ---------- tray ----------

    private void InitTray()
    {
        if (_tray is not null) return;
        _tray = new TrayController(this)
        {
            IsEffectsRunning = () => _engine?.IsRunning == true,
            ToggleEffects = start => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (start) { _engine?.Apply(CurrentProfile()); RefreshStatus(); }
                    else { _engine?.Stop(); SetStatus(L10n.T("status.stopped"), StatusKind.Info); }
                    SyncRunButtons();
                }
                catch { }
            }),
            Blackout = () => Dispatcher.BeginInvoke(() => { try { Blackout_Click(this, new RoutedEventArgs()); } catch { } }),
            GamingOn = () => Dispatcher.BeginInvoke(() => { try { ApplyGamingEverywhere(); } catch { } }),
            ProfileNames = () => App.Settings.Profiles.Select(p => p.Name).ToList(),
            ActiveProfile = () => CurrentProfile().Name,
            SelectProfile = name => Dispatcher.BeginInvoke(() => { try { SwitchProfile(name); } catch { } }),
            ExitApp = () => Dispatcher.BeginInvoke(() =>
            {
                _reallyExiting = true;
                try { Application.Current.Shutdown(); } catch { }
            }),
        };
    }

    public void HideToTray()
    {
        InitTray();
        Hide();
    }

    /// <summary>
    /// Tray "Gaming lights": every paintable zone switches to the Gaming effect (screen
    /// mirror + hit flash) for THIS session, overriding the profile without rewriting it.
    /// The user gets instant game-reactive lighting; the next profile switch returns to it.
    /// </summary>
    private void ApplyGamingEverywhere()
    {
        var profile = CurrentProfile();
        var gaming = new Effects.EffectDef
        {
            Type = Effects.EffectType.Gaming,
            ColorHex = profile.GlobalEffect.ColorHex,   // no-screen fallback keeps the profile's colour
            Brightness = profile.GlobalEffect.Brightness,
            BeatStrength = 1.0,
            SyncZones = true,
        };
        foreach (var dev in _client?.Controllers ?? Enumerable.Empty<SDK.RgbController>())
            profile.DeviceOverrides[dev.Key] = EffectEngine.Clone(gaming);
        _target = TargetMode.Device;   // the editor shows the override target, not the global effect
        _selectedKey = null;
        _selectedZone = -1;
        _edit = EffectEngine.Clone(gaming);
        _engine?.Apply(profile);
        SetStatus(L10n.T("tray.gaming"), StatusKind.Ok);
        BuildDeviceList();
        BuildDevicePicker();
        BuildEffectEditor();
        RefreshStatus();
        SyncRunButtons();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window keeps the effects running and parks the app in the tray;
        // the tray's Exit item is the way out. This is what users expect from RGB software.
        // Never block OS shutdown/logoff: that would hang reboot.
        if (e is System.ComponentModel.CancelEventArgs)
        {
            bool shuttingDown = false;
            try
            {
                // CloseReason is on the WinForms EventArgs; WPF passes plain CancelEventArgs,
                // so detect session-ending via Application.SessionEnding registration below.
                shuttingDown = _osShuttingDown;
            }
            catch { }
            if (!_reallyExiting && !shuttingDown)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { _resumeHeartbeat?.Stop(); } catch { }
        try { _powerMonitor?.Dispose(); } catch { }
        try { _lightingWatchdog?.Dispose(); } catch { }
        try { StopPreview(); } catch { }
        StopAutomation();
        try { _engineWatchdog?.Stop(); } catch { }
        try
        {
            if (_engine is not null && _engineStatusHandler is not null)
                _engine.Status -= _engineStatusHandler;
        }
        catch { }
        try { _engine?.Stop(); } catch { }
        try { _audio?.Dispose(); } catch { }
        try { _temps.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        try
        {
            if (App.Settings.CloseEngineOnExit)
                _mgr?.StopIncludingElevated();
            else
                _mgr?.Stop();
        }
        catch { }
        try { _tray?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    // ---------- status ----------

    private enum StatusKind { Info, Ok, Warn, Error, Busy }

    private void SetStatus(string msg, StatusKind kind)
    {
        StatusTxt.Text = msg;
        StatusDot.Fill = (Brush)FindResource(kind switch
        {
            StatusKind.Ok => "Ok",
            StatusKind.Warn => "Warn",
            StatusKind.Error => "Danger",
            StatusKind.Busy => "Accent",
            _ => "Faint",
        });
    }
}


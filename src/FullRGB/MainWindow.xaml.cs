using System.Diagnostics;
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
            StartPreview();   // the hero preview must animate even if the SDK never connects
            if (Headless) { BuildEffectEditor(); LoadProfileToUi(); BuildDevicePicker(); TickPreview(); return; }
            InitTray();   // tray icon exists for the whole session, not only when minimised
            if (_client is { Connected: true }) OnConnected();
            else await ConnectAsync();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
    }

    // ---------- connection ----------

    private void OnConnected()
    {
        var profile = CurrentProfile();
        // Drop overrides for hardware that is no longer here, so settings.json cannot grow forever.
        if (_client is not null) profile.PruneTo(_client.Controllers);

        StartEngine();
        _edit = EffectEngine.Clone(profile.GlobalEffect);
        BuildDeviceList();
        BuildDevicePicker();
        BuildEffectEditor();
        LoadProfileToUi();
        RefreshStatus();
        CheckIcue();
        CheckSmbus();
    }

    private void RefreshStatus()
    {
        if (_client is null) return;
        int leds = _client.Controllers.Sum(c => c.LedCount);
        // The pill and the subtitle must NOT say the same thing: the pill is the live state,
        // the subtitle is the inventory. Duplicating one sentence in both places also made both
        // of them truncate.
        _inventory = L10n.T("dev.inventory", _client.Controllers.Count, leds);
        SubTitleTxt.Text = _inventory;
        SetStatus(L10n.T("status.ready"), StatusKind.Ok);
        _tray?.SetTooltip(L10n.T("tray.tip", _inventory));
        UpdateDiagnostics();
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
        try { _audio = new AudioProvider(); _audio.Start(); }
        catch { _audio = null; _audioFailed = true; }

        _engine = new EffectEngine(_client, _temps, _audio);
        _engine.Status += m => Dispatcher.BeginInvoke(() => SetStatus(m, StatusKind.Warn));

        // If OpenRGB itself dies, restart the process and rebuild the session.
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

        StartPreview();
        if (App.Settings.AutoStartEffects) _engine.Apply(CurrentProfile());
        SyncRunButtons();
    }

    private Profile CurrentProfile() =>
        App.Settings.Profiles.FirstOrDefault(x => x.Name == App.Settings.ActiveProfile)
        ?? App.Settings.Profiles[0];

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
        FlowDirection = fa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        LangBtn.Content = fa ? "EN" : "فا";
        StatusPill.ToolTip = L10n.T("btn.rescan");
        TabLighting.Content = L10n.T("tab.lighting");
        TabDevices.Content = L10n.T("tab.devices");
        TabHardware.Content = L10n.T("tab.hardware");
        TabSettings.Content = L10n.T("tab.settings");
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

    /// <summary>Switches the visible page when a nav item is picked.</summary>
    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (PageLighting is null) return;   // fires during InitializeComponent
        PageLighting.Visibility = TabLighting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDevices.Visibility = TabDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHardware.Visibility = TabHardware.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
            ToggleEffects = start => Dispatcher.Invoke(() =>
            {
                if (start) { _engine?.Apply(CurrentProfile()); RefreshStatus(); }
                else { _engine?.Stop(); SetStatus(L10n.T("status.stopped"), StatusKind.Info); }
                SyncRunButtons();
            }),
            Blackout = () => Dispatcher.Invoke(() => Blackout_Click(this, new RoutedEventArgs())),
            ProfileNames = () => App.Settings.Profiles.Select(p => p.Name).ToList(),
            ActiveProfile = () => CurrentProfile().Name,
            SelectProfile = name => Dispatcher.Invoke(() => SwitchProfile(name)),
            ExitApp = () => Dispatcher.Invoke(() =>
            {
                _reallyExiting = true;
                Application.Current.Shutdown();
            }),
        };
    }

    public void HideToTray()
    {
        InitTray();
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window keeps the effects running and parks the app in the tray;
        // the tray's Exit item is the way out. This is what users expect from RGB software.
        if (!_reallyExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPreview();
        _engine?.Stop();
        _audio?.Dispose();
        _temps.Dispose();
        _client?.Dispose();
        if (App.Settings.CloseEngineOnExit)
            _mgr?.StopIncludingElevated();
        else
            _mgr?.Stop();
        _tray?.Dispose();
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

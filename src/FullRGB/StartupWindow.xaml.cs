using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullRGB.SDK;
using FullRGB.Setup;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace FullRGB;

/// <summary>
/// First screen: checks required drivers, downloads/installs the missing ones with a
/// real progress bar, starts the RGB engine, scans devices and expands addressable zones.
/// Hands a fully connected client to MainWindow.
/// </summary>
public partial class StartupWindow : Window
{
    public static string? StartupWarning { get; set; }

    public OpenRgbProcessManager? Manager { get; private set; }
    public OpenRgbClient? Client { get; private set; }
    public bool Ready { get; private set; }

    private CancellationTokenSource _cts = new();

    public StartupWindow()
    {
        InitializeComponent();
        FlowDirection = L10n.IsRtl ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
        TitleTxt.Text = L10n.T("scan.title");
        SkipBtn.Content = L10n.T("scan.skip");
        // --uitest/--uishot must never start the engine or prompt for UAC.
        if (!MainWindow.Headless) Loaded += async (_, _) => { ShowPlaceholder(); await RunAsync(); };
        else { Stage(L10n.T("scan.step.detect"), 62); ShowPlaceholder(); }
    }

    /// <summary>Placeholder line shown until the first real log entry arrives.</summary>
    private TextBlock? _placeholder;

    private void Log(string text, Brush? color = null)
    {
        if (_placeholder is not null)
        {
            LogPanel.Children.Remove(_placeholder);
            _placeholder = null;
        }
        LogPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = color ?? (Brush)FindResource("Faint"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        LogScroll.ScrollToBottom();
    }

    /// <summary>An empty log card looks hung; say what we are waiting for.</summary>
    private void ShowPlaceholder()
    {
        if (LogPanel.Children.Count > 0) return;
        _placeholder = new TextBlock
        {
            Text = L10n.T("scan.waiting"),
            Foreground = (Brush)FindResource("Faint"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        LogPanel.Children.Add(_placeholder);
    }

    private void Stage(string text, double percent = -1)
    {
        StageTxt.Text = text;
        if (percent < 0)
        {
            Bar.IsIndeterminate = true;
            PercentTxt.Text = "";
            BytesTxt.Text = "";
        }
        else
        {
            Bar.IsIndeterminate = false;
            Bar.Value = percent;
            // a bare bar with no number reads as "possibly stuck"
            PercentTxt.Text = $"{percent:F0}%";
        }
    }

    private async Task RunAsync()
    {
        if (!string.IsNullOrEmpty(StartupWarning))
            Log("settings: " + StartupWarning, (Brush)FindResource("Warn"));

        // ---- 1. optional drivers ----
        // FullRGB never installs anything or asks for admin on its own: the HID path (board
        // headers, Commander Core, ARGB fans) needs neither. PawnIO only unlocks RGB RAM/GPU,
        // so its absence is reported here and installing it is a button in Settings → Advanced.
        Stage(L10n.T("scan.step.deps"));
        await Task.Delay(150);

        foreach (var dep in DependencyManager.All)
        {
            if (dep.IsInstalled()) Log(L10n.T("scan.dep.ok", dep.Name), (Brush)FindResource("Accent"));
            else Log(L10n.T("scan.dep.optional", dep.Name, dep.Why), (Brush)FindResource("Faint"));
        }

        await ContinueToEngineAsync();
    }

    private async Task ContinueToEngineAsync()
    {
        ActionBtn.Visibility = Visibility.Collapsed;
        SkipBtn.Visibility = Visibility.Collapsed;

        // ---- 2. engine ----
        Stage(L10n.T("scan.step.engine"));
        try
        {
            Manager = new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath(), App.Settings.ServerPort);
            await Manager.StartAsync(TimeSpan.FromSeconds(75));
        }
        catch (Exception e)
        {
            Log("engine: " + e.Message, (Brush)FindResource("Danger"));
            ActionBtn.Content = L10n.T("scan.retry");
            ActionBtn.Visibility = Visibility.Visible;
            return;
        }

        // ---- 3. detect ----
        Stage(L10n.T("scan.step.detect"));
        // Detection continues for several seconds AFTER the port opens; connecting too early
        // returns a short device list (this is why fans/coolers appeared "missing").
        await Task.Delay(Manager.AttachedToExisting ? 500 : 5000);
        try
        {
            Client = new OpenRgbClient();
            await Task.Run(() => Client!.Connect("127.0.0.1", App.Settings.ServerPort, "FullRGB"), _cts.Token);

            // Wait until the device list stops growing (bounded), so late HID/SMBus devices are included.
            int stable = 0, last = Client.Controllers.Count;
            for (int i = 0; i < 12 && stable < 2; i++)
            {
                await Task.Delay(900, _cts.Token);
                await Task.Run(() => Client!.RefreshControllers(_cts.Token), _cts.Token);
                int now = Client.Controllers.Count;
                stable = now == last ? stable + 1 : 0;
                last = now;
                Stage(L10n.T("scan.step.detect") + $" ({now})");
            }
        }
        catch (Exception e)
        {
            Log("sdk: " + e.Message, (Brush)FindResource("Danger"));
            ActionBtn.Content = L10n.T("scan.retry");
            ActionBtn.Visibility = Visibility.Visible;
            return;
        }

        foreach (var dev in Client.Controllers)
            Log($"• {dev.Name} — {dev.Zones.Count} zones");

        // ---- 4. expand addressable zones (this is what makes effects visible) ----
        Stage(L10n.T("scan.step.zones"));
        var profile = App.Settings.Profiles.FirstOrDefault(p => p.Name == App.Settings.ActiveProfile)
                      ?? App.Settings.Profiles[0];
        await Task.Run(() => Client!.ExpandAllZones(_cts.Token, (d, z) => profile.ZoneSize(d, z)), _cts.Token);
        await Task.Run(() => Client!.EnsureDirectMode(), _cts.Token);

        int totalLeds = Client.Controllers.Sum(c => c.LedCount);
        Stage(L10n.T("scan.step.done", Client.Controllers.Count, totalLeds), 100);
        foreach (var dev in Client.Controllers)
            Log($"✓ {dev.Name} — {L10n.T("dev.leds", dev.LedCount)}", (Brush)FindResource("Accent"));

        if (Manager.LastRunHadSmbusFailure() && !Client.Controllers.Any(c => c.Kind == RgbDeviceType.DRAM))
            Log(L10n.T("smbus.warn"), (Brush)FindResource("Warn"));

        Ready = true;
        await Task.Delay(900);
        DialogResult = true;
        Close();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        // The only remaining use of this button is "Retry" after an engine/SDK failure.
        ActionBtn.Visibility = Visibility.Collapsed;
        SkipBtn.Visibility = Visibility.Collapsed;
        await ContinueToEngineAsync();
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        SkipBtn.Visibility = Visibility.Collapsed;
        await ContinueToEngineAsync();
    }

    /// <summary>
    /// The splash had no way out: a stuck engine start meant Task Manager. The X cancels
    /// the whole startup (Ready stays false, so App shuts down cleanly).
    /// </summary>
    private void Abort_Click(object sender, RoutedEventArgs e)
    {
        try { _cts.Cancel(); } catch { }
        Ready = false;
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!Ready)
        {
            _cts.Cancel();
            Manager?.Stop();
        }
        base.OnClosed(e);
    }
}

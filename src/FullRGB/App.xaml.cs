using System.Threading;
using System.Windows;
using FullRGB.Config;
using Application = System.Windows.Application;

namespace FullRGB;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --selftest / --fxtest: headless hardware verification (must run BEFORE UAC relaunch)
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Run on the thread pool: awaiting on the Dispatcher context with GetResult() would deadlock.
            var code = Task.Run(() => SelfTest.RunAsync(e.Args)).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }
        if (e.Args.Any(a => a.Equals("--fxtest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int secs = 15;
            var sArg = e.Args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
            if (sArg is not null && int.TryParse(sArg.Split('=')[1], out var parsed)) secs = parsed;
            var code = Task.Run(() => SelfTest.RunEffectTestAsync(secs)).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }
        // --rendertest: pure-logic checks (no hardware) for colour consistency, audio gate,
        // calibration and tray-icon decoding.
        if (e.Args.Any(a => a.Equals("--rendertest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RenderTests.Run());
            return;
        }
        // --uitest: instantiate every window/dialog headlessly so XAML errors and missing
        // resource keys fail the build gate instead of the user's first launch.
        if (e.Args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            FullRGB.MainWindow.Headless = true;   // 'MainWindow' alone binds to Application.MainWindow here
            Settings = new AppSettings();
            L10n.Set("en");
            Theme.ApplyAccent(Settings.AccentHex);
            Shutdown(UiTests.Run());
            return;
        }
        // --uishot: render the windows to PNG for layout review (no hardware, no focus steal)
        if (e.Args.Any(a => a.Equals("--uishot", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            FullRGB.MainWindow.Headless = true;   // 'MainWindow' alone binds to Application.MainWindow here
            Settings = new AppSettings();
            L10n.Set(e.Args.Any(a => a.Equals("--fa", StringComparison.OrdinalIgnoreCase)) ? "fa" : "en");
            Theme.ApplyAccent(Settings.AccentHex);
            Shutdown(UiShots.Run(e.Args));
            return;
        }

        // --enginetask=status|register|remove: headless control of the elevated engine task.
        // Exists so the RGB-RAM path can be verified and repaired without the GUI (and so this
        // agent could test the real code, not a re-implementation of it).
        var taskArg = e.Args.FirstOrDefault(a => a.StartsWith("--enginetask", StringComparison.OrdinalIgnoreCase));
        if (taskArg is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string verb = taskArg.Contains('=') ? taskArg.Split('=')[1].ToLowerInvariant() : "status";
            int port = ProfileStore.Load().ServerPort;
            string exe = SDK.OpenRgbProcessManager.DefaultExePath();
            string err = "";
            int code = 0;
            switch (verb)
            {
                case "register":
                    code = Setup.EngineTask.Register(exe, port, out err) ? 0 : 1;
                    Console.WriteLine($"register: {(code == 0 ? "OK" : "FAILED " + err)}");
                    break;
                case "remove":
                case "unregister":
                    code = Setup.EngineTask.Unregister(out err) ? 0 : 1;
                    Console.WriteLine($"remove: {(code == 0 ? "OK" : "FAILED " + err)}");
                    break;
                case "run":
                    code = Setup.EngineTask.Run(out err) ? 0 : 1;
                    Console.WriteLine($"run: {(code == 0 ? "OK" : "FAILED " + err)}");
                    break;
                default:
                    Console.WriteLine($"registered={Setup.EngineTask.IsRegistered()} " +
                                      $"matchesThisInstall={Setup.EngineTask.MatchesInstall(exe)} " +
                                      $"taskExe={Setup.EngineTask.RegisteredExePath() ?? "-"} " +
                                      $"pawnio={Setup.DependencyManager.IsPawnIoInstalled()} " +
                                      $"elevated={SDK.Elevation.IsElevated} exe={exe} port={port}");
                    break;
            }
            Shutdown(code);
            return;
        }

        // --usbscan: list every present USB/HID device with its VID:PID and the product string the
        // DEVICE reports. Used to answer "why isn't my mouse in the list?" with evidence.
        if (e.Args.Any(a => a.Equals("--usbscan", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            foreach (var d in Diag.UsbScan.Scan())
                Console.WriteLine($"{d.VidPid}  {d.DeviceClass,-12} {d.Label}");
            Shutdown(0);
            return;
        }

        // Only one FullRGB may drive the hardware at a time: two engines fight over the SDK port
        // and each one's zone resizes/mode switches undo the other's.
        _singleInstance = new Mutex(true, @"Global\FullRGB_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show(
                L10n.T("err.alreadyRunning"),
                "FullRGB", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // NO automatic elevation. Everything on the HID path (motherboard headers, Corsair
        // Commander Core, ARGB fans) works as a normal user; only RGB RAM/GPU need SMBus, which
        // needs PawnIO + admin. That is offered explicitly in Settings → Advanced instead of
        // forcing a UAC prompt on every launch.
        Settings = ProfileStore.Load();
        StartupWindow.StartupWarning = ProfileStore.LastLoadError;
        L10n.Set(Settings.Language);
        Theme.ApplyAccent(Settings.AccentHex);

        var startup = new StartupWindow();
        startup.ShowDialog();

        if (startup.Ready && startup.Client is not null)
        {
            var win = new MainWindow(startup.Manager, startup.Client);
            // Always Show() first: WPF only raises Loaded on a shown window, and Loaded is
            // where the tray icon and the effect engine come up. Start-minimised then hides it.
            win.Show();
            bool startMinimized = Settings.StartMinimized
                || e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (startMinimized) win.HideToTray();
        }
        else
        {
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ProfileStore.Save(Settings);
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

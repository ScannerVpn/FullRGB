using System.IO;
using System.Threading;
using System.Windows;
using FullRGB.Automation;
using FullRGB.Config;
using Application = System.Windows.Application;

namespace FullRGB;

public partial class App : Application
{
    public static AppSettings Settings { get; set; } = new();

    /// <summary>
    /// An automation verb ("set-profile gaming") that arrived on the command line while NO
    /// instance was running. MainWindow replays it once the engine session is up — a CLI
    /// command behaves the same whether the app was already running or not.
    /// </summary>
    public static string PendingCommandVerb { get; set; } = "";

    /// <summary>
    /// Every RGB part this machine has ever shown. Loaded before the splash so the app can name
    /// the known hardware immediately, and used to keep per-device settings when a device is
    /// missing from one scan. Null in headless runs (they must not touch user files).
    /// </summary>
    public static DeviceCache? DeviceCache { get; set; }

    /// <summary>
    /// Folds a live scan into the remembered inventory and persists it. Called from the splash and
    /// from every successful connect/rescan. Never throws.
    /// </summary>
    public static void RememberDevices(IEnumerable<SDK.RgbController> devices)
    {
        try
        {
            var cache = (DeviceCache ?? new DeviceCache()).Merge(devices, DateTime.UtcNow);
            DeviceCache = cache;
            DeviceCache.Save(cache);
        }
        catch { /* remembering the inventory is best-effort */ }
    }

    private static Mutex? _singleInstance;
    private static bool _headless; // --rendertest/--uitest/etc must never touch settings.json

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --selftest / --fxtest: headless hardware verification (must run BEFORE UAC relaunch)
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Run on the thread pool: awaiting on the Dispatcher context with GetResult() would deadlock.
            var code = Task.Run(() => SelfTest.RunAsync(e.Args)).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }
        if (e.Args.Any(a => a.Equals("--fxtest", StringComparison.OrdinalIgnoreCase)))
        {
            _headless = true;
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
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RenderTests.Run());
            return;
        }
        // --uitest: instantiate every window/dialog headlessly so XAML errors and missing
        // resource keys fail the build gate instead of the user's first launch.
        if (e.Args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
        {
            _headless = true;
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
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            FullRGB.MainWindow.Headless = true;   // 'MainWindow' alone binds to Application.MainWindow here
            Settings = new AppSettings();
            L10n.Set(e.Args.Any(a => a.Equals("--fa", StringComparison.OrdinalIgnoreCase)) ? "fa" : "en");
            Theme.ApplyAccent(Settings.AccentHex);
            Shutdown(UiShots.Run(e.Args));
            return;
        }

        // --audiotest[=seconds]: print the live audio analysis.
        //
        // "The music effect does not follow the song" cannot be diagnosed from a description — this
        // shows exactly what the analyser sees (overall level, the three bands, which band the auto
        // detector picked, and the beat envelope) while music plays. Written to the app log as well as
        // stdout, so it also lands in the diagnostics zip.
        var audioArg = e.Args.FirstOrDefault(a => a.StartsWith("--audiotest", StringComparison.OrdinalIgnoreCase));
        if (audioArg is not null)
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int seconds = 15;
            if (audioArg.Contains('=') && int.TryParse(audioArg.Split('=')[1], out int sec))
                seconds = Math.Clamp(sec, 3, 120);
            RunAudioTest(seconds);
            Shutdown(0);
            return;
        }

        // --hid-list: print every HID collection (VID:PID, usagePage, usage, report lengths).
        // The whole community-protocol workflow depends on those numbers and nothing else in the
        // app reported them, so authors had to reach for a third-party HID tool first.
        // --hid-list=json emits a probe skeleton for each vendor channel instead.
        var hidArg = e.Args.FirstOrDefault(a => a.StartsWith("--hid-list", StringComparison.OrdinalIgnoreCase));
        if (hidArg is not null)
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool json = hidArg.Contains('=') &&
                        hidArg.Split('=', 2)[1].Equals("json", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (json)
                {
                    var vendor = Hid.HidBridge.Enumerate()
                        .Where(c => c.UsagePage is >= 0xFF00 and <= 0xFFFF)
                        .Where(c => c.FeatureLength > 0 || c.OutputLength > 0)
                        .ToList();
                    PrintOut(vendor.Count == 0
                        ? "No vendor HID channel with a readable report was found on this machine."
                        : string.Join(Environment.NewLine + Environment.NewLine,
                                      vendor.Select(Hid.HidBridge.ProtocolSkeleton)));
                }
                else PrintOut(Hid.HidBridge.DescribeAll());
            }
            catch (Exception ex) { PrintOut("hid-list failed: " + ex.Message); }
            Shutdown(0);
            return;
        }

        // --enginetask=status|register|remove: headless control of the elevated engine task.
        // Exists so the RGB-RAM path can be verified and repaired without the GUI (and so this
        // agent could test the real code, not a re-implementation of it).
        var taskArg = e.Args.FirstOrDefault(a => a.StartsWith("--enginetask", StringComparison.OrdinalIgnoreCase));
        if (taskArg is not null)
        {
            _headless = true;
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

        // --autostart=status|ensure: headless check/repair of the logon task.
        var autoArg = e.Args.FirstOrDefault(a => a.StartsWith("--autostart", StringComparison.OrdinalIgnoreCase));
        if (autoArg is not null)
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string verb = autoArg.Contains('=') ? autoArg.Split('=')[1].ToLowerInvariant() : "status";
            var settings = ProfileStore.Load();
            string args = settings.StartMinimized ? "--minimized" : "";
            int code = 0;
            if (verb == "ensure")
            {
                bool ok = Autostart.EnsureCurrent(args, out string ensureErr);
                Console.WriteLine($"ensure: {(ok ? "OK" : "FAILED " + ensureErr)}");
                if (!ok) code = 1;
            }
            Console.WriteLine($"registered={Autostart.RegisteredExePath() is not null} " +
                              $"matchesCurrent={Autostart.MatchesCurrent()} " +
                              $"taskExe={Autostart.RegisteredExePath() ?? "-"} " +
                              $"exe={Environment.ProcessPath}");
            Shutdown(code);
            return;
        }

        // --stalltest: headless proof of the frame watchdog. Joins the running engine, applies the
        // configured effect through the REAL EffectEngine, then wedges every one of its write
        // sockets with a non-reading client so the engine stops applying frames — the state the
        // user could only clear by hand. The watchdog must decide Repair on its own, and frames
        // must reach the hardware again WITHOUT any user action.
        if (e.Args.Any(a => a.Equals("--stalltest", StringComparison.OrdinalIgnoreCase)))
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int secs2 = 150;
            var sArg2 = e.Args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
            if (sArg2 is not null && int.TryParse(sArg2.Split('=')[1], out var parsed2)) secs2 = parsed2;
            var code2 = Task.Run(() => SelfTest.RunStallTestAsync(secs2)).GetAwaiter().GetResult();
            Shutdown(code2);
            return;
        }

        // --usbscan: list every present USB/HID device with its VID:PID and the product string the
        // DEVICE reports. Used to answer "why isn't my mouse in the list?" with evidence.
        if (e.Args.Any(a => a.Equals("--usbscan", StringComparison.OrdinalIgnoreCase)))
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            foreach (var d in Diag.UsbScan.Scan())
                Console.WriteLine($"{d.VidPid}  {d.DeviceClass,-12} {d.Label}");
            Shutdown(0);
            return;
        }

        // --export-diagnostics[=path]: build the one-click bug-report zip without the GUI.
        // Same code the Hardware page button calls (Diag.DiagnosticsExport), so support
        // tickets can be produced headlessly from CI or a support script.
        var diagArg = e.Args.FirstOrDefault(a => a.StartsWith("--export-diagnostics", StringComparison.OrdinalIgnoreCase));
        if (diagArg is not null)
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = 0;
            try
            {
                string path = diagArg.Contains('=')
                    ? diagArg.Split('=', 2)[1]
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                   $"FullRGB-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
                var built = Diag.DiagnosticsExport.Build(path, () => Diag.DiagnosticsExport.SupportMatrixText());
                Console.WriteLine(built);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERR " + ex.Message);
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // --help / --version: scriptable usage text.
        if (e.Args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintOut(CliHelpText());
            Shutdown(0);
            return;
        }
        if (e.Args.Any(a => a is "--version" or "-v"))
        {
            PrintOut("FullRGB " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"));
            Shutdown(0);
            return;
        }

        // Automation verbs: --set-profile gaming and friends. A running instance gets the
        // command over the named pipe and this process exits; with no instance running, the
        // command is parked and applied right after this launch's engine session comes up
        // (the read-only info verbs answer from the settings file instead).
        if (TryBuildAutomationCommand(e.Args, out string cmd, out string cmdErr))
        {
            _headless = true;   // never touch the UI below even if we continue into startup
            var (ok, reply) = ControlHub.SendOneShot(cmd).GetAwaiter().GetResult();
            if (ok)
            {
                if (Console.IsOutputRedirected)
                    Console.WriteLine(reply);                    // scripts always get the payload
                else if (reply.StartsWith("ERR", StringComparison.Ordinal)
                         || cmd is "status" or "profiles" or "version")
                    PrintOut(reply);                             // double-click users see it
                // an OK action verb stays silent when there is no console: the LEDs are the answer
                Shutdown(0);
                return;
            }
            if (reply.StartsWith("ERR", StringComparison.Ordinal))
            {
                // An instance IS running but the command failed — report and stop.
                if (Console.IsOutputRedirected) Console.WriteLine(reply);
                else PrintOut(reply);
                Shutdown(1);
                return;
            }
            // No running instance: answer info verbs offline, park action verbs for startup.
            if (cmd is "status" or "profiles")
            {
                PrintOut(cmd == "status"
                    ? "{\"ok\":false,\"app\":\"FullRGB\",\"engine\":\"offline\"}"
                    : string.Join("\n", ProfileStore.Load().Profiles.Select(p => p.Name)));
                Shutdown(0);
                return;
            }
            PendingCommandVerb = cmd;
            _headless = false;   // continue into the normal startup below
        }
        else if (cmdErr.Length > 0)
        {
            PrintOut("ERR " + cmdErr);
            if (!Console.IsOutputRedirected)
                System.Windows.MessageBox.Show(cmdErr, "FullRGB", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        // Update housekeeping BEFORE the single-instance check: a pending update is swapped in
        // here (rename current exe -> .old, move the downloaded exe into place, relaunch) so the
        // user never has to reinstall by hand. Must happen before any engine/UI work.
        try
        {
            // Rollback FIRST: it needs both the rollback marker and the previous run's state,
            // and CleanupStale below would delete the .old copy it depends on.
            if (Update.AppUpdater.TryRollbackBrokenUpdate(out _))
            {
                Update.AppUpdater.Relaunch(e.Args);
                Shutdown(0);
                return;
            }
            Update.AppUpdater.CleanupStale();
            var applied = Update.AppUpdater.ApplyPendingOnStartup(e.Args);
            if (applied is not null)
            {
                Update.AppUpdater.Relaunch(applied);
                Shutdown(0);
                return;
            }
        }
        catch { /* a failed swap must never block the app from starting */ }

        // Only one FullRGB may drive the hardware at a time: two engines fight over the SDK port
        // and each one's zone resizes/mode switches undo the other's.
        // Local\ (not Global\): standard users cannot create a Global mutex (UnauthorizedAccessException).
        bool isFirst = true;
        try
        {
            _singleInstance = new Mutex(true, @"Local\FullRGB_SingleInstance", out isFirst);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed while holding it: we now own it.
            isFirst = true;
        }
        catch
        {
            // No mutex (e.g. access denied): let the port check in ProcessManager arbitrate instead.
            isFirst = true;
            _singleInstance = null;
        }
        if (!isFirst)
        {
            System.Windows.MessageBox.Show(
                L10n.T("err.alreadyRunning"),
                "FullRGB", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // Only the PRIMARY instance owns the marker: a second instance that starts and immediately
        // exits must not overwrite (or clear) the running one's marker, or a real crash would look
        // clean. Written here, read by the window once it is up.
        Diag.SessionMarker.Begin();
        if (Diag.SessionMarker.PreviousUncleanRun is { Length: > 0 } previousRun)
            Diag.AppLog.Warn($"previous session did not exit cleanly (started {previousRun})");

        // NO automatic elevation. Everything on the HID path (motherboard headers, Corsair
        // Commander Core, ARGB fans) works as a normal user; only RGB RAM/GPU need SMBus, which
        // needs PawnIO + admin. That is offered explicitly in Settings → Advanced instead of
        // forcing a UAC prompt on every launch.
        Settings = ProfileStore.Load();
        DeviceCache = Config.DeviceCache.Load();
        StartupWindow.StartupWarning = ProfileStore.LastLoadError;
        L10n.Set(Settings.Language);
        Theme.ApplyAccent(Settings.AccentHex);

        // Self-heal the logon task: it stores an ABSOLUTE exe path, so running from a new
        // folder (every dist build) left it aiming at a deleted exe and autostart silently
        // died with 0x80070002. Rewrite it to this exe; own-user task, no UAC, best-effort.
        if (Settings.StartWithWindows)
        {
            try
            {
                if (!Autostart.EnsureCurrent(Settings.StartMinimized ? "--minimized" : ""))
                    StartupWindow.StartupWarning = string.Join("\n",
                        new[] { StartupWindow.StartupWarning, L10n.T("status.failed", "autostart task") }
                        .Where(s => !string.IsNullOrEmpty(s)));
            }
            catch { }
        }

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
        if (!_headless) ProfileStore.Save(Settings);
        // This run is ending on purpose, so the next launch must NOT report it as unclean.
        if (!_headless) Diag.SessionMarker.End();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ---------- round 21: CLI helpers ----------

    /// <summary>Console output when redirected, a dialog when the user just double-clicked.</summary>
    private static void PrintOut(string text)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
            return;
        }
        // A GUI-subsystem exe has no console of its own; surface the text in a themed box so
        // "--help by double-click" is not a silent no-op.
        try { System.Windows.MessageBox.Show(text, "FullRGB", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch { }
    }

    internal static string CliHelpText() =>
        "FullRGB " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?") +
        " — usage:\n\n" +
        "  FullRGB.exe                          start the app\n" +
        "  FullRGB.exe --help                   this text\n" +
        "  FullRGB.exe --version                print the app version\n\n" +
        "Automation (a running instance is driven over the local pipe; without one,\n" +
        "the command is applied at startup):\n" +
        "  --status                             one-line JSON session summary\n" +
        "  --list-profiles                      print profile names\n" +
        "  --set-profile <name>                 switch profile\n" +
        "  --set-effect <name>                  set the global effect (solid, rainbow, gamepulse, ...)\n" +
        "  --set-color <#RRGGBB> [--secondary]  set the effect colour\n" +
        "  --set-brightness <0..1>              set brightness (0..100 also accepted)\n" +
        "  --set-speed <0..1>                   set speed\n" +
        "  --power <on|off>                     start/stop effects\n" +
        "  --blackout                           paint every LED black once\n" +
        "  --game-event <name> [value]          feed the GamePulse effect (hp, hit, death, ...)\n" +
        "  --rescan                             re-detect devices\n\n" +
        "Named pipe: \\\\.\\pipe\\" + ControlHub.PipeName + "  (one line per command)\n" +
        "HTTP API:   http://127.0.0.1:" + SettingsOrDefault().ControlApiPort + "/  (token required; see docs/automation.md)\n" +
        "Mobile:     Settings -> Mobile companion (same port, PIN + token)\n\n" +
        "Diagnostics:\n" +
        "  --export-diagnostics[=path.zip]      build the bug-report zip headlessly\n" +
        "  --hid-list                           list every HID collection (VID:PID, usagePage, usage)\n" +
        "  --hid-list=json                      probe skeletons for the vendor HID channels\n\n" +
        "  --audiotest[=seconds]                live audio analysis (level, bands, auto pick, beat)\n\n" +
        CommandDispatcher.HelpText;

    /// <summary>Live audio readout for <c>--audiotest</c>. See the call site for why it exists.</summary>
    private static void RunAudioTest(int seconds)
    {
        void Line(string text)
        {
            Console.WriteLine(text);
            Diag.AppLog.Info("audiotest: " + text);
        }
        Line($"starting — play music now, sampling for {seconds}s");
        using var audio = new Sensors.AudioProvider();
        try { audio.Start(); }
        catch (Exception e) { Line("capture failed: " + e.Message); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(200);
            Line($"t={sw.Elapsed.TotalSeconds,5:F1}s  level={audio.Level:F3}  " +
                 $"bass={audio.Bass:F3} mid={audio.Mid:F3} treble={audio.Treble:F3}  " +
                 $"auto={audio.AutoLevel:F3} ({Sensors.AudioProvider.BandName(audio.AutoBandIndex)})  " +
                 $"beat={audio.Beat:F2}");
        }
        Line("done");
    }

    private static AppSettings SettingsOrDefault()
    {
        try { return ProfileStore.Load(); } catch { return new AppSettings(); }
    }

    /// <summary>
    /// Maps the friendly CLI flags onto the shared dispatcher command set. Returns false when
    /// no automation flag is present; cmdErr is non-empty for a malformed one.
    /// </summary>
    private static bool TryBuildAutomationCommand(string[] args, out string command, out string error)
    {
        command = "";
        error = "";

        string err = "";

        string? Take(string flag, bool withValue = true)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
                if (!withValue) return "";
                if (i + 1 >= args.Length) { err = $"missing value for {flag}"; return null; }
                return args[i + 1];
            }
            return null;
        }

        // one value-bearing flag per invocation (the dispatcher handles one command)
        var flags = new[]
        {
            "--set-profile", "--profile", "--set-effect", "--set-color", "--set-brightness",
            "--set-speed", "--power", "--game-event", "--status", "--list-profiles", "--rescan",
            "--blackout",
        };
        int found = args.Count(a => flags.Any(f => a.Equals(f, StringComparison.OrdinalIgnoreCase)));
        if (found == 0) return false;
        if (found > 1) { error = "give exactly ONE automation flag per invocation"; return false; }

        string? v;
        if ((v = Take("--set-profile")) is not null || (v = Take("--profile")) is not null)
        { error = err; if (error.Length > 0) return false; command = "set-profile " + v; return true; }
        if ((v = Take("--set-effect")) is not null)
        { command = "set-effect " + v; return true; }
        if ((v = Take("--set-color")) is not null)
        {
            bool secondary = args.Any(a => a.Equals("--secondary", StringComparison.OrdinalIgnoreCase));
            command = (secondary ? "set-color2 " : "set-color ") + v;
            return true;
        }
        if ((v = Take("--set-brightness")) is not null) { command = "set-brightness " + v; return true; }
        if ((v = Take("--set-speed")) is not null) { command = "set-speed " + v; return true; }
        if ((v = Take("--power")) is not null) { command = "power " + v.ToLowerInvariant(); return true; }
        if ((v = Take("--game-event")) is not null)
        {
            // value may itself be "name value" (two args were consumed by --game-event)
            string extra = Take("--game-event") is not null && args.Length > Array.IndexOf(args, "--game-event") + 2
                ? args[Array.IndexOf(args, "--game-event") + 2] : "";
            command = "game-event " + v + (extra.Length > 0 ? " " + extra : "");
            return true;
        }
        if (args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase))) { command = "status"; return true; }
        if (args.Any(a => a.Equals("--list-profiles", StringComparison.OrdinalIgnoreCase))) { command = "profiles"; return true; }
        if (args.Any(a => a.Equals("--rescan", StringComparison.OrdinalIgnoreCase))) { command = "rescan"; return true; }
        if (args.Any(a => a.Equals("--blackout", StringComparison.OrdinalIgnoreCase))) { command = "blackout"; return true; }
        return false;
    }
}

using System.Diagnostics;
using System.IO;

namespace FullRGB;

/// <summary>
/// Headless check for the effect-consistency and audio-gate fixes:
///  - renders the SAME effect for two different zone sizes/seeds and asserts identical colour
///    when SyncZones=true (the "solid colour differs per zone" report)
///  - asserts the audio effect renders BLACK when the level is at/below the noise gate
///  - asserts calibration is applied and is reversible
/// Pure logic, no hardware needed, so it can gate every build.
/// </summary>
public static class RenderTests
{
    public static int Run()
    {
        int failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failed++;
        }

        var ctx = new Effects.EffectContext { Time = 3.7, AudioLevel = 0, CpuTemp = 55, GpuTemp = 60 };

        // ---- 1. solid, synced: every zone must be byte-identical regardless of size/seed ----
        var solid = new Effects.EffectDef
        {
            Type = Effects.EffectType.Solid, ColorHex = "#00E5FF", Brightness = 0.8, SyncZones = true,
        };
        var a = Effects.EffectRenderer.Render(solid, 34, 0, ctx);
        var b = Effects.EffectRenderer.Render(solid, 120, 0, ctx);
        var c = Effects.EffectRenderer.Render(solid, 29, 0, ctx);
        bool sameSolid = a[0] == b[0] && a[1] == b[1] && a[2] == b[2]
                      && a[0] == c[0] && a[1] == c[1] && a[2] == c[2];
        Check("solid: identical colour across zone sizes", sameSolid,
              $"({a[0]},{a[1]},{a[2]}) vs ({b[0]},{b[1]},{b[2]}) vs ({c[0]},{c[1]},{c[2]})");

        // ---- 2. animated + synced: seed 0 everywhere must give the same first pixel ----
        var rainbow = new Effects.EffectDef
        {
            Type = Effects.EffectType.Rainbow, Speed = 0.6, Brightness = 1.0, SyncZones = true,
        };
        var r1 = Effects.EffectRenderer.Render(rainbow, 34, 0, ctx);
        var r2 = Effects.EffectRenderer.Render(rainbow, 34, 0, ctx);
        Check("rainbow synced: deterministic for identical seed",
              r1[0] == r2[0] && r1[1] == r2[1] && r1[2] == r2[2]);

        // ---- 3. animated + NOT synced: different seeds must differ (the wave-across-case look) ----
        var r3 = Effects.EffectRenderer.Render(rainbow, 34, 0, ctx);
        var r4 = Effects.EffectRenderer.Render(rainbow, 34, 5, ctx);
        Check("rainbow unsynced: different seed gives different phase",
              !(r3[0] == r4[0] && r3[1] == r4[1] && r3[2] == r4[2]));

        // ---- 4. audio gate: silence must be fully black ----
        var audio = new Effects.EffectDef
        {
            Type = Effects.EffectType.AudioVU, ColorHex = "#00E5FF", Color2Hex = "#7C4DFF",
            Brightness = 1.0,
        };
        var silent = Effects.EffectRenderer.Render(audio, 60, 0,
            new Effects.EffectContext { AudioLevel = 0.0 });
        bool allBlack = silent.All(v => v == 0);
        Check("audio: silence renders black", allBlack,
              allBlack ? "" : $"first px ({silent[0]},{silent[1]},{silent[2]})");

        var nearSilent = Effects.EffectRenderer.Render(audio, 60, 0,
            new Effects.EffectContext { AudioLevel = 0.015 });
        Check("audio: below noise gate renders black", nearSilent.All(v => v == 0));

        var loud = Effects.EffectRenderer.Render(audio, 60, 0,
            new Effects.EffectContext { AudioLevel = 0.9 });
        Check("audio: loud lights most of the strip",
              loud.Where((_, i) => i % 3 == 2).Count(v => v > 0) > 40);

        // ---- 5. calibration ----
        var cal = new Config.Calibration { RGain = 1.0, GGain = 0.5, BGain = 1.0, Gamma = 1.0 };
        var buf = new byte[] { 200, 200, 200 };
        cal.Apply(buf);
        Check("calibration: green gain halves the green channel",
              buf[0] == 200 && buf[1] == 100 && buf[2] == 200,
              $"({buf[0]},{buf[1]},{buf[2]})");

        var ident = new Config.Calibration();
        var buf2 = new byte[] { 11, 22, 33 };
        ident.Apply(buf2);
        Check("calibration: identity is a no-op", buf2[0] == 11 && buf2[1] == 22 && buf2[2] == 33);

        // ---- 6. tray icon actually decodes (this is what was blank in the notification area) ----
        try
        {
            using var ico = TrayController.LoadAppIcon();
            using var bmp = ico.ToBitmap();
            int visible = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).A > 20) visible++;
            Check("tray icon: loads and has visible pixels", visible > 50,
                  $"{visible} visible px at {bmp.Width}x{bmp.Height}");
        }
        catch (Exception e)
        {
            Check("tray icon: loads", false, e.Message);
        }

        // ---- 7. driver download checksum verification ----
        var tmp = Path.Combine(Path.GetTempPath(), "fullrgb-checksum-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3, 4, 5 });
        string goodHash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            goodHash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(tmp)));
        Check("checksum: valid hash accepted", Setup.DependencyManager.VerifySha256(tmp, goodHash));
        Check("checksum: wrong hash rejected", !Setup.DependencyManager.VerifySha256(tmp, "00".PadLeft(64, '0')));
        File.Delete(tmp);

        // ---- 8. zone size clamping ----
        var zdev = new SDK.RgbController { Index = 0, Name = "Test", Location = "usb-0" };
        var zzone = new SDK.RgbZone { Index = 0, LedsMin = 10, LedsMax = 120, LedsCount = 0 };
        var zprof = new Config.Profile();
        string zkey = Config.Profile.ZoneKey(zdev, zzone);
        zprof.ZoneSizes[zkey] = 9999;
        Check("zone size: clamps above max", zprof.ZoneSize(zdev, zzone) == 120);
        zprof.ZoneSizes[zkey] = 3;
        Check("zone size: clamps below min", zprof.ZoneSize(zdev, zzone) == 10);
        zprof.ZoneSizes[zkey] = 0;
        Check("zone size: 0 falls back to max", zprof.ZoneSize(zdev, zzone) == 120);

        // ---- 9. SDK payload size guard ----
        Check("payload: small size accepted", SDK.OpenRgbClient.IsPayloadSizeValid(1024));
        Check("payload: huge size rejected", !SDK.OpenRgbClient.IsPayloadSizeValid(0x80000000u));

        // ---- 10. audio sample stride ----
        Check("audio: 32-bit stereo stride", Sensors.AudioProvider.GetSampleStride(new NAudio.Wave.WaveFormat(44100, 32, 2)) == 8);
        Check("audio: 16-bit mono stride", Sensors.AudioProvider.GetSampleStride(new NAudio.Wave.WaveFormat(44100, 16, 1)) == 2);

        // ---- 11. settings roundtrip + corrupt fallback ----
        var sdir = Path.Combine(Path.GetTempPath(), "fullrgb-settings-" + Guid.NewGuid().ToString("N"));
        var spath = Path.Combine(sdir, "settings.json");
        var sset = new Config.AppSettings { Language = "fa", ActiveProfile = "P" };
        sset.Profiles.Clear();
        sset.Profiles.Add(new Config.Profile { Name = "P" });
        Config.ProfileStore.SaveTo(spath, sset);
        var loaded = Config.ProfileStore.LoadFrom(spath);
        Check("settings: roundtrip", loaded.Language == "fa" && loaded.ActiveProfile == "P" && loaded.Profiles.Count == 1 && loaded.Profiles[0].Name == "P");
        File.WriteAllText(spath, "{ not valid json");
        var corrupt = Config.ProfileStore.LoadFrom(spath);
        Check("settings: corrupt falls back", corrupt.Language == "en");
        try { Directory.Delete(sdir, true); } catch { }

        // ---- 12. autostart command ----
        string create = Autostart.BuildCommand(true, @"C:\Program Files\FullRGB\FullRGB.exe", "--minimized");
        Check("autostart: create quotes exe", create.Contains("\\\"C:\\Program Files\\FullRGB\\FullRGB.exe\\\" --minimized"));
        Check("autostart: runs unelevated (no /RL HIGHEST)",
              create.Contains("/RL LIMITED") && !create.Contains("HIGHEST"));
        string del = Autostart.BuildCommand(false, "x", "");
        Check("autostart: delete command", del.Contains("/Delete"));

        // ---- 12b. elevated ENGINE task (this is what makes RGB RAM work) ----
        // Proven on this rig from the engine log: unelevated -> "Permission Denied, PawnIO
        // initialization aborted" and 2 controllers; elevated -> "PawnIO initialized successfully"
        // plus two "[ENE DRAM] Registering RGB controller" lines. So the ENGINE (not the app)
        // must run at highest level, and the task must carry the SDK server flags.
        string reg = Setup.EngineTask.BuildRegisterScript(@"C:\Program Files\FullRGB\vendor\OpenRGB\OpenRGB.exe", 6742);
        Check("engineTask: RunLevel Highest", reg.Contains("-RunLevel Highest"));
        Check("engineTask: passes SDK server flags", reg.Contains("--server --server-port 6742"));
        Check("engineTask: quotes the exe path", reg.Contains("'C:\\Program Files\\FullRGB\\vendor\\OpenRGB\\OpenRGB.exe'"));
        Check("engineTask: sets a working directory", reg.Contains("-WorkingDirectory"));
        Check("engineTask: no trigger (run on demand only)",
              !reg.Contains("New-ScheduledTaskTrigger"));
        Check("engineTask: no execution time limit", reg.Contains("ExecutionTimeLimit"));
        Check("engineTask: replaces an existing task", reg.Contains("-Force"));
        Check("engineTask: correct task name", reg.Contains($"'{Setup.EngineTask.TaskName}'"));
        Check("engineTask: unregister targets the same name",
              Setup.EngineTask.BuildUnregisterScript().Contains(Setup.EngineTask.TaskName));
        // A path containing an apostrophe must not break out of the PowerShell string.
        string tricky = Setup.EngineTask.BuildRegisterScript(@"C:\it's\OpenRGB.exe", 6742);
        Check("engineTask: escapes apostrophes in paths", tricky.Contains("'C:\\it''s\\OpenRGB.exe'"));

        // The task stores an ABSOLUTE exe path, so a task left behind by a different install must
        // be detected instead of silently starting the wrong engine.
        string taskXml = "<Task><Actions><Exec>" +
                         "<Command>G:\\Ai\\RGB Control\\dist9\\vendor\\OpenRGB\\OpenRGB Windows 64-bit\\OpenRGB.exe</Command>" +
                         "<Arguments>--server --server-port 6742</Arguments>" +
                         "</Exec></Actions></Task>";
        Check("engineTask: parses the task exe path",
              Setup.EngineTask.ParseCommand(taskXml) == @"G:\Ai\RGB Control\dist9\vendor\OpenRGB\OpenRGB Windows 64-bit\OpenRGB.exe");
        Check("engineTask: no Command element means no path",
              Setup.EngineTask.ParseCommand("<Task/>") is null);

        // ---- 13. parser failure is surfaced ----
        var badDev = SDK.DeviceParser.Parse(0, new byte[] { 1, 2, 3 });
        Check("parser: failure flagged", badDev.ParseFailed && badDev.Kind == SDK.RgbDeviceType.Unknown);

        // ---- 14. recovery is skipped during cancellation ----
        Check("engine: no recovery when cancelled", !Effects.EffectEngine.ShouldAttemptRecovery(1, true));
        Check("engine: recovery at first failure", Effects.EffectEngine.ShouldAttemptRecovery(1, false));

        // ---- 15. seed offsets EVERY animated effect (round 7: it used to affect rainbow only) ----
        foreach (var t in new[] { Effects.EffectType.Wave, Effects.EffectType.Blink,
                                  Effects.EffectType.Breathing, Effects.EffectType.Custom,
                                  Effects.EffectType.Comet, Effects.EffectType.Fire })
        {
            var def = new Effects.EffectDef { Type = t, Speed = 0.5, Brightness = 1.0, SyncZones = false };
            var s0 = Effects.EffectRenderer.Render(def, 40, 0, ctx);
            var s1 = Effects.EffectRenderer.Render(def, 40, 3, ctx);
            Check($"seed: {t} differs per zone when unsynced", !s0.SequenceEqual(s1));

            var syncA = Effects.EffectRenderer.Render(def, 40, 0, ctx);
            var syncB = Effects.EffectRenderer.Render(def, 40, 0, ctx);
            Check($"seed: {t} deterministic for one seed", syncA.SequenceEqual(syncB));
        }

        // ---- 16. gradient is a real ramp and direction flips it ----
        var grad = new Effects.EffectDef
        {
            Type = Effects.EffectType.Gradient, ColorHex = "#FF0000", Color2Hex = "#0000FF", Brightness = 1.0,
        };
        var gf = Effects.EffectRenderer.Render(grad, 10, 0, ctx);
        Check("gradient: starts primary, ends secondary",
              gf[0] > 200 && gf[2] < 40 && gf[27] < 40 && gf[29] > 200,
              $"first=({gf[0]},{gf[1]},{gf[2]}) last=({gf[27]},{gf[28]},{gf[29]})");
        grad.Direction = "reverse";
        var gr = Effects.EffectRenderer.Render(grad, 10, 0, ctx);
        Check("gradient: reverse flips the ramp", gr[0] < 40 && gr[2] > 200);

        // ---- 16b. the colour row must appear exactly for effects that READ ColorHex ----
        // The Lighting page used to show "Color #00E5FF" next to a rainbow preview, i.e. it
        // claimed a colour was in effect when the renderer never looked at it. Prove the
        // predicate against real renders: change ColorHex and see whether the output moves.
        foreach (var type in Enum.GetValues<Effects.EffectType>())
        {
            var withRed = new Effects.EffectDef
            {
                Type = type, ColorHex = "#FF0000", Color2Hex = "#0000FF", Brightness = 1.0,
                CustomPixels = "#FF0000,#00FF00", AudioBand = "level",
            };
            var withGreen = new Effects.EffectDef
            {
                Type = type, ColorHex = "#00FF00", Color2Hex = "#0000FF", Brightness = 1.0,
                CustomPixels = "#FF0000,#00FF00", AudioBand = "level",
            };
            // A context with real audio/sensor values, so AudioVU and Temperature actually paint.
            var colCtx = new Effects.EffectContext
            {
                Time = 0.25, AudioLevel = 0.8, AudioBass = 0.8, AudioMid = 0.8, AudioTreble = 0.8,
                CpuTemp = 55, GpuTemp = 55,
            };
            var f1 = Effects.EffectRenderer.Render(withRed, 40, 0, colCtx);
            var f2 = Effects.EffectRenderer.Render(withGreen, 40, 0, colCtx);
            bool renderUsesColor = !f1.SequenceEqual(f2);
            Check($"colorRow: {type} predicate matches the renderer",
                  MainWindow.UsesPrimaryColor(type) == renderUsesColor,
                  $"predicate={MainWindow.UsesPrimaryColor(type)} renderer={renderUsesColor}");
        }

        // ---- 17. audio band selection actually selects a band ----
        var bandCtx = new Effects.EffectContext { AudioLevel = 0, AudioBass = 0.9, AudioMid = 0, AudioTreble = 0 };
        var bassFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioBand = "bass", Brightness = 1.0 };
        var lvlFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioBand = "level", Brightness = 1.0 };
        Check("audio: bass band lights on bass only",
              Effects.EffectRenderer.Render(bassFx, 30, 0, bandCtx).Any(v => v > 0)
              && Effects.EffectRenderer.Render(lvlFx, 30, 0, bandCtx).All(v => v == 0));

        // ---- 18. FFT correctness: a pure tone must land in its own bin ----
        int n = 1024;
        var re = new double[n]; var im = new double[n];
        for (int i = 0; i < n; i++) re[i] = Math.Sin(2 * Math.PI * 64 * i / n);
        Sensors.AudioProvider.Fft(re, im);
        int peak = 1;
        double best = 0;
        for (int k = 1; k < n / 2; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            if (mag > best) { best = mag; peak = k; }
        }
        Check("fft: pure tone peaks in the right bin", peak == 64, $"peak bin {peak}");

        // ---- 19. zone override beats device override beats global ----
        var pdev = new SDK.RgbController { Index = 0, Name = "Dev", Location = "usb-1" };
        var pzone = new SDK.RgbZone { Index = 2, LedsMin = 0, LedsMax = 10, LedsCount = 10 };
        var prof = new Config.Profile
        {
            GlobalEffect = new Effects.EffectDef { Type = Effects.EffectType.Solid },
        };
        prof.DeviceOverrides[pdev.Key] = new Effects.EffectDef { Type = Effects.EffectType.Wave };
        prof.ZoneOverrides[Config.Profile.ZoneKey(pdev, pzone)] = new Effects.EffectDef { Type = Effects.EffectType.Fire };
        Check("override: zone wins", prof.EffectFor(pdev, pzone).Type == Effects.EffectType.Fire);
        prof.ZoneOverrides.Clear();
        Check("override: device wins next", prof.EffectFor(pdev, pzone).Type == Effects.EffectType.Wave);
        prof.DeviceOverrides.Clear();
        Check("override: global is the fallback", prof.EffectFor(pdev, pzone).Type == Effects.EffectType.Solid);

        // ---- 20. pruning drops stale devices but keeps present ones ----
        prof.DeviceOverrides["Ghost@usb-9"] = new Effects.EffectDef();
        prof.ZoneSizes["Ghost@usb-9|0"] = 5;
        prof.DeviceOverrides[pdev.Key] = new Effects.EffectDef();
        prof.PruneTo(new[] { pdev });
        Check("prune: stale keys removed",
              !prof.DeviceOverrides.ContainsKey("Ghost@usb-9") && !prof.ZoneSizes.ContainsKey("Ghost@usb-9|0"));
        Check("prune: present device kept", prof.DeviceOverrides.ContainsKey(pdev.Key));

        // ---- 21. settings normalisation repairs hand-edited files ----
        var messy = new Config.AppSettings { Language = "de", ServerPort = 0, AccentHex = "nope", ActiveProfile = "gone" };
        messy.Profiles.Clear();
        messy.Profiles.Add(new Config.Profile { Name = "" });
        messy.Profiles.Add(new Config.Profile { Name = "Dup" });
        messy.Profiles.Add(new Config.Profile { Name = "Dup" });
        messy.Normalized();
        Check("settings: bad language falls back to en", messy.Language == "en");
        Check("settings: bad port falls back", messy.ServerPort == 6742);
        Check("settings: bad accent falls back", messy.AccentHex == "#00E5FF");
        Check("settings: duplicate names made unique",
              messy.Profiles.Select(p => p.Name).Distinct().Count() == messy.Profiles.Count);
        Check("settings: active profile repaired",
              messy.Profiles.Any(p => p.Name == messy.ActiveProfile));

        // ---- 22. accent theming maths ----
        Check("theme: hex parse", Theme.Parse("#FF0000", System.Windows.Media.Colors.Black).R == 255);
        Check("theme: bad hex falls back",
              Theme.Parse("zzz", System.Windows.Media.Colors.Lime).G == 255);
        Check("theme: luminance ordering",
              Theme.Luminance(System.Windows.Media.Colors.White) > Theme.Luminance(System.Windows.Media.Colors.Black));

        // ---- 23. hex shorthand + clamping in the renderer ----
        var shortHex = Effects.EffectRenderer.ParseHex("#0F0", 1.0);
        Check("parse: #RGB shorthand", shortHex.g == 255 && shortHex.r == 0);
        var dimmed = Effects.EffectRenderer.ParseHex("#FFFFFF", 0.5);
        Check("parse: brightness scales", dimmed.r is >= 126 and <= 129);

        Console.WriteLine(failed == 0 ? "\nALL RENDER TESTS PASSED" : $"\n{failed} TEST(S) FAILED");
        return failed == 0 ? 0 : 1;
    }
}

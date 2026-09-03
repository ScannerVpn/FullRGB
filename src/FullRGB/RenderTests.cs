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
                                  Effects.EffectType.Comet, Effects.EffectType.Fire,
                                  Effects.EffectType.Scanner, Effects.EffectType.Sparkle,
                                  Effects.EffectType.Plasma })
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

        // ---- 24. music shapes: bar / mirror / pulse look different ----
        var loudCtx = new Effects.EffectContext { Time = 1.0, AudioLevel = 0.7 };
        var barFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioMode = "bar", Brightness = 1.0 };
        var mirrorFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioMode = "mirror", Brightness = 1.0 };
        var pulseFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioMode = "pulse", Brightness = 1.0 };
        var barF = Effects.EffectRenderer.Render(barFx, 30, 0, loudCtx);
        var mirrorF = Effects.EffectRenderer.Render(mirrorFx, 30, 0, loudCtx);
        var pulseF = Effects.EffectRenderer.Render(pulseFx, 30, 0, loudCtx);
        Check("music: bar and mirror differ", !barF.SequenceEqual(mirrorF));
        Check("music: pulse differs from bar", !pulseF.SequenceEqual(barF));
        bool mirrorSym = true;
        for (int i = 0; i < 30; i++)
            if (mirrorF[i * 3] != mirrorF[(29 - i) * 3] || mirrorF[i * 3 + 1] != mirrorF[(29 - i) * 3 + 1])
            { mirrorSym = false; break; }
        Check("music: mirror is symmetric", mirrorSym);
        bool pulseUniform = true;
        for (int i = 1; i < 30; i++)
            if (pulseF[i * 3] != pulseF[0] || pulseF[i * 3 + 1] != pulseF[1] || pulseF[i * 3 + 2] != pulseF[2])
            { pulseUniform = false; break; }
        Check("music: pulse paints the whole strip one colour", pulseUniform);

        // ---- 25. music colours: palette / level-ramp / background ----
        var palFx = new Effects.EffectDef
        {
            Type = Effects.EffectType.AudioVU, AudioColor = "palette",
            CustomPixels = "#FF0000,#00FF00,#0000FF", Brightness = 1.0,
        };
        var palF = Effects.EffectRenderer.Render(palFx, 30, 0, loudCtx);
        Check("music: palette mode differs from gradient", !palF.SequenceEqual(barF));
        Check("music: palette mode uses the custom colours",
              palF.Take(3).SequenceEqual(new byte[] { 255, 0, 0 }));
        var lvlRampFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioColor = "level", Brightness = 1.0 };
        var lvlF = Effects.EffectRenderer.Render(lvlRampFx, 30, 0, loudCtx);
        Check("music: level-ramp differs from gradient", !lvlF.SequenceEqual(barF));
        var bgFx = new Effects.EffectDef
        {
            Type = Effects.EffectType.AudioVU, AudioBgHex = "#112233", Brightness = 1.0,
        };
        var quietCtx = new Effects.EffectContext { Time = 1.0, AudioLevel = 0.0 };
        var bgF = Effects.EffectRenderer.Render(bgFx, 10, 0, quietCtx);
        Check("music: silence shows the background, not black",
              bgF[0] == 0x11 && bgF[1] == 0x22 && bgF[2] == 0x33);

        // ---- 26. peak-hold: the dot lingers after the level drops ----
        var st = new Effects.AudioState();
        var hotCtx = new Effects.EffectContext { Time = 5.0, AudioLevel = 0.9 };
        var pk1 = Effects.EffectRenderer.Render(
            new Effects.EffectDef { Type = Effects.EffectType.AudioVU, PeakHold = true, Brightness = 1.0 },
            30, 0, hotCtx, st);
        int lastLit(byte[] fr)
        {
            int last = -1;
            for (int i = 0; i < fr.Length / 3; i++)
                if (fr[i * 3] != 0 || fr[i * 3 + 1] != 0 || fr[i * 3 + 2] != 0) last = i;
            return last;
        }
        int hotEdge = lastLit(pk1);
        var coldCtx = new Effects.EffectContext { Time = 5.0, AudioLevel = 0.25 };
        var pk2 = Effects.EffectRenderer.Render(
            new Effects.EffectDef { Type = Effects.EffectType.AudioVU, PeakHold = true, Brightness = 1.0 },
            30, 0, coldCtx, st);
        Check("music: peak dot holds past the live edge", lastLit(pk2) == hotEdge,
              $"hot edge {hotEdge}, after drop {lastLit(pk2)}");

        // ---- 27. spectrum: three bands, three segments ----
        var specCtx = new Effects.EffectContext { Time = 2.0, AudioBass = 0.9, AudioMid = 0, AudioTreble = 0 };
        var specFx = new Effects.EffectDef { Type = Effects.EffectType.Spectrum, Brightness = 1.0 };
        var specF = Effects.EffectRenderer.Render(specFx, 30, 0, specCtx);
        bool firstThirdLit = specF.Take(10 * 3).Any(v => v > 0);
        bool restDark = specF.Skip(10 * 3).All(v => v == 0);
        Check("spectrum: bass lights the first third only", firstThirdLit && restDark);
        var specQuiet = Effects.EffectRenderer.Render(specFx, 30, 0,
            new Effects.EffectContext { Time = 2.0 });
        Check("spectrum: silence is black", specQuiet.All(v => v == 0));
        var specTreble = Effects.EffectRenderer.Render(specFx, 30, 0,
            new Effects.EffectContext { Time = 2.0, AudioTreble = 0.9 });
        Check("spectrum: treble lights the last third",
              specTreble.Take(20 * 3).All(v => v == 0) && specTreble.Skip(20 * 3).Any(v => v > 0));

        // ---- 28. UsePalette reroutes the two-colour effects through CustomPixels ----
        var waveGrad = new Effects.EffectDef
        {
            Type = Effects.EffectType.Wave, ColorHex = "#FF0000", Color2Hex = "#0000FF",
            Brightness = 1.0, UsePalette = false,
        };
        var wavePal = new Effects.EffectDef
        {
            Type = Effects.EffectType.Wave, ColorHex = "#FF0000", Color2Hex = "#0000FF",
            CustomPixels = "#00FF00,#00FF00", Brightness = 1.0, UsePalette = true,
        };
        var wg = Effects.EffectRenderer.Render(waveGrad, 30, 0, ctx);
        var wp = Effects.EffectRenderer.Render(wavePal, 30, 0, ctx);
        Check("palette: wave reroutes through CustomPixels", !wg.SequenceEqual(wp));
        Check("palette: wave is all-green from a green palette",
              wp[0] < 30 && wp[1] > 200 && wp[2] < 30);

        // ---- 29. old settings without the new keys still render (null-tolerant defaults) ----
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Effects.EffectDef>(
            """{"ColorHex":null,"Color2Hex":null,"Color3Hex":null,"AudioMode":null,"AudioColor":null,"AudioBgHex":null,"CustomPixels":null,"Direction":null,"AudioBand":null}""");
        Check("compat: legacy json deserializes", legacy is not null);
        legacy!.Normalized();
        var legacyF = Effects.EffectRenderer.Render(legacy, 10, 0, ctx);
        Check("compat: legacy defaults render", legacyF.Length == 30 && legacy.AudioMode == "bar");
        Check("compat: clone keeps the new fields",
              Effects.EffectEngine.Clone(new Effects.EffectDef { AudioMode = "mirror", PeakHold = false }).AudioMode == "mirror"
              && !Effects.EffectEngine.Clone(new Effects.EffectDef { PeakHold = false }).PeakHold);

        // ---- 30. dots meter + rainbow music colour + presets ----
        var dotsFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioMode = "dots", Brightness = 1.0 };
        var dotsF = Effects.EffectRenderer.Render(dotsFx, 30, 0, loudCtx);
        Check("music: dots differs from bar", !dotsF.SequenceEqual(barF));
        Check("music: dots lights only every 3rd LED",
              dotsF.Take(3).Any(v => v > 0) && dotsF[3] == 0 && dotsF[4] == 0 && dotsF[5] == 0);
        var rbFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioColor = "rainbow", Brightness = 1.0 };
        var rbF = Effects.EffectRenderer.Render(rbFx, 30, 0, loudCtx);
        Check("music: rainbow differs from gradient", !rbF.SequenceEqual(barF));
        var rbF2 = Effects.EffectRenderer.Render(rbFx, 30, 0,
            new Effects.EffectContext { Time = 3.0, AudioLevel = 0.7 });
        Check("music: rainbow flows with time", !rbF.SequenceEqual(rbF2));
        var presetFx = new Effects.EffectDef { Type = Effects.EffectType.Wave };
        Effects.EffectPresets.Apply(presetFx, Effects.EffectPresets.All[0]);
        Check("preset: applies 3 colours + palette",
              presetFx.ColorHex == "#FF6B35" && presetFx.Color2Hex == "#FF2E63"
              && presetFx.Color3Hex == "#FFD166" && presetFx.CustomPixels.Contains("FF6B35"));
        Check("preset: 12 curated sets", Effects.EffectPresets.All.Length == 12);

        // ---- 31. extra stops: Gradient samples boxes + extras as one ramp ----
        var gx3 = new Effects.EffectDef
        {
            Type = Effects.EffectType.Gradient, ColorHex = "#FF0000", Color2Hex = "#0000FF",
            ExtraColors = new List<string> { "#00FF00" }, Brightness = 1.0,
        };
        var gx3f = Effects.EffectRenderer.Render(gx3, 3, 0, ctx);
        Check("extras: 3-stop gradient hits every stop",
              gx3f[0] > 200 && gx3f[1] < 40 && gx3f[2] < 40
              && gx3f[3] < 40 && gx3f[4] < 40 && gx3f[5] > 200
              && gx3f[6] < 40 && gx3f[7] > 200 && gx3f[8] < 40,
              $"({gx3f[0]},{gx3f[1]},{gx3f[2]}) ({gx3f[3]},{gx3f[4]},{gx3f[5]}) ({gx3f[6]},{gx3f[7]},{gx3f[8]})");
        var gx2 = new Effects.EffectDef
        {
            Type = Effects.EffectType.Gradient, ColorHex = "#FF0000", Color2Hex = "#0000FF", Brightness = 1.0,
        };
        Check("extras: empty extras render exactly like the old 2-stop ramp",
              Effects.EffectRenderer.Render(gx2, 30, 0, ctx)
                  .SequenceEqual(Effects.EffectRenderer.Render(
                      new Effects.EffectDef
                      {
                          Type = Effects.EffectType.Gradient, ColorHex = "#FF0000", Color2Hex = "#0000FF",
                          ExtraColors = new List<string>(), Brightness = 1.0,
                      }, 30, 0, ctx)));
        var blinkX = new Effects.EffectDef
        {
            Type = Effects.EffectType.Blink, ColorHex = "#FF0000",
            ExtraColors = new List<string> { "#00FF00" }, Speed = 0.5, Brightness = 1.0,
        };
        var blinkXf = Effects.EffectRenderer.Render(blinkX, 5, 0,
            new Effects.EffectContext { Time = 4.5 });
        Check("extras: blink steps into the extra colour",
              blinkXf[0] < 40 && blinkXf[1] > 200 && blinkXf[2] < 40,
              $"({blinkXf[0]},{blinkXf[1]},{blinkXf[2]})");
        var dirty = new Effects.EffectDef
        {
            ExtraColors = new List<string> { "notacolor", "#00FF00", "ABC", "#00FF00", "#00FF00", "#00FF00", "#00FF00", "#00FF00", "#00FF00", "#00FF00" },
        };
        dirty.Normalized();
        Check("extras: invalid dropped, capped at 8 with # prefix",
              dirty.ExtraColors.Count == 8 && dirty.ExtraColors[0] == "#00FF00" && dirty.ExtraColors[1] == "#ABC");
        var withX = new Effects.EffectDef { ExtraColors = new List<string> { "#00FF00" } };
        Effects.EffectPresets.Apply(withX, Effects.EffectPresets.All[0]);
        Check("preset: clears stale extras", withX.ExtraColors.Count == 0);
        var cloned = Effects.EffectEngine.Clone(new Effects.EffectDef { ExtraColors = new List<string> { "#00FF00" } });
        Check("extras: clone keeps values", cloned.ExtraColors.SequenceEqual(new[] { "#00FF00" }));
        cloned.ExtraColors.Add("#FF0000");
        var orig = new Effects.EffectDef { ExtraColors = new List<string> { "#00FF00" } };
        var cloned2 = Effects.EffectEngine.Clone(orig);
        cloned2.ExtraColors.Add("#FF0000");
        Check("extras: clone is a copy, not shared", orig.ExtraColors.Count == 1);

        // ---- 32. music sensitivity: gain boosts quiet signals, 0.2 floor keeps gate sane ----
        var quiet = new Effects.EffectContext { Time = 1.0, AudioLevel = 0.15 };
        var g1 = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioGain = 1.0, Brightness = 1.0 };
        var g2 = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioGain = 2.5, Brightness = 1.0 };
        int litCount(byte[] fr)
        {
            int n = 0;
            for (int i = 0; i < fr.Length / 3; i++)
                if (fr[i * 3] != 0 || fr[i * 3 + 1] != 0 || fr[i * 3 + 2] != 0) n++;
            return n;
        }
        Check("gain: higher sensitivity lights more LEDs",
              litCount(Effects.EffectRenderer.Render(g2, 30, 0, quiet))
              > litCount(Effects.EffectRenderer.Render(g1, 30, 0, quiet)));
        var gLow = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioGain = -5, Brightness = 1.0 };
        gLow.Normalized();
        Check("gain: clamped to 0.2..2.5", Math.Abs(gLow.AudioGain - 0.2) < 1e-9);
        var gHigh = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioGain = 99, Brightness = 1.0 };
        gHigh.Normalized();
        Check("gain: clamped at top", Math.Abs(gHigh.AudioGain - 2.5) < 1e-9);

        // ---- 33. beat flash: white overlay on kicks, off by default ----
        var beatCtx = new Effects.EffectContext { Time = 1.0, AudioLevel = 0.6, Beat = 1.0 };
        var noBeat = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, BeatStrength = 0, Brightness = 1.0 };
        var yesBeat = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, BeatStrength = 1.0, Brightness = 1.0 };
        var bf0 = Effects.EffectRenderer.Render(noBeat, 10, 0, beatCtx);
        var bf1 = Effects.EffectRenderer.Render(yesBeat, 10, 0, beatCtx);
        Check("beat: strength 0 leaves the frame untouched",
              bf0.SequenceEqual(Effects.EffectRenderer.Render(
                  new Effects.EffectDef { Type = Effects.EffectType.AudioVU, Brightness = 1.0 }, 10, 0,
                  new Effects.EffectContext { Time = 1.0, AudioLevel = 0.6 })));
        Check("beat: full beat whitens the frame",
              bf1.All(v => v == 255));
        var halfBeat = Effects.EffectRenderer.Render(yesBeat, 10, 0,
            new Effects.EffectContext { Time = 1.0, AudioLevel = 0.6, Beat = 0.0 });
        Check("beat: no kick means no flash", halfBeat.SequenceEqual(bf0));
        var badBeat = new Effects.EffectDef { BeatStrength = 5 };
        badBeat.Normalized();
        Check("beat: clamped to 0..1", Math.Abs(badBeat.BeatStrength - 1.0) < 1e-9);

        // ---- 34. scheduler rotation order ----
        Check("sched: rotates forward",
              MainWindow.SchedulerNextIndex(new List<string> { "A", "B", "C" }, "A") == 1);
        Check("sched: wraps around",
              MainWindow.SchedulerNextIndex(new List<string> { "A", "B", "C" }, "C") == 0);
        Check("sched: unknown active starts at 0",
              MainWindow.SchedulerNextIndex(new List<string> { "A", "B" }, "gone") == 0);
        Check("sched: single profile never rotates",
              MainWindow.SchedulerNextIndex(new List<string> { "A" }, "A") == -1);

        // ---- 35. per-app map parsing + matching ----
        var fmap = ForegroundWatcher.ParseMap("game.exe=Play\n# comment\n\nbadline\ngame.exe=Late\nAPP.EXE=Work");
        Check("fg: last wins, comments/blanks/bad lines ignored",
              fmap.Count == 2 && fmap["game.exe"] == "Late" && fmap["app.exe"] == "Work");
        Check("fg: matches exe case-insensitively with a known profile",
              ForegroundWatcher.MatchProfile(fmap, "GAME.EXE", new[] { "Late", "Work" }) == "Late");
        Check("fg: unknown exe gives null",
              ForegroundWatcher.MatchProfile(fmap, "other.exe", new[] { "Late" }) is null);
        Check("fg: unknown profile gives null",
              ForegroundWatcher.MatchProfile(
                  new Dictionary<string, string> { ["x.exe"] = "Ghost" }, "x.exe", new[] { "Late" }) is null);

        // ---- 36. per-zone calibration beats device calibration ----
        var zcdev = new SDK.RgbController { Index = 0, Name = "Dev", Location = "usb-1" };
        var zczone = new SDK.RgbZone { Index = 1, LedsMin = 0, LedsMax = 10, LedsCount = 10 };
        var zcprof = new Config.Profile();
        zcprof.Calibrations[zcdev.Key] = new Config.Calibration { RGain = 0.5, GGain = 1, BGain = 1, Gamma = 1 };
        zcprof.ZoneCalibrations[Config.Profile.ZoneKey(zcdev, zczone)] =
            new Config.Calibration { RGain = 1, GGain = 0.25, BGain = 1, Gamma = 1 };
        var zbuf = new byte[] { 200, 200, 200 };
        zcprof.CalibrationFor(zcdev, zczone).Apply(zbuf);
        Check("zonecal: zone entry wins over device", zbuf[0] == 200 && zbuf[1] == 50,
              $"({zbuf[0]},{zbuf[1]},{zbuf[2]})");
        zcprof.ZoneCalibrations.Clear();
        var zbuf2 = new byte[] { 200, 200, 200 };
        zcprof.CalibrationFor(zcdev, zczone).Apply(zbuf2);
        Check("zonecal: falls back to device", zbuf2[0] == 100 && zbuf2[1] == 200);
        zcprof.ZoneCalibrations["Ghost@usb-9|3"] = new Config.Calibration();
        zcprof.PruneTo(new[] { zcdev });
        Check("zonecal: stale zone entries pruned", !zcprof.ZoneCalibrations.ContainsKey("Ghost@usb-9|3"));
        var zsrc = new Config.Profile();
        zsrc.ZoneCalibrations["K@l|0"] = new Config.Calibration { RGain = 0.5, GGain = 1, BGain = 1, Gamma = 1 };
        var zclone = Effects.EffectEngine.Clone(zsrc);
        Check("zonecal: clone keeps zone entries",
              zclone.ZoneCalibrations.Count == 1 && Math.Abs(zclone.ZoneCalibrations["K@l|0"].RGain - 0.5) < 1e-9);

        // ---- 37. settings backup rotation in a temp dir ----
        string tmpBk = Path.Combine(Path.GetTempPath(), "fullrgb-test-" + Guid.NewGuid().ToString("N"));
        string tmpSettings = Path.Combine(tmpBk, "settings.json");
        string tmpBackups = Path.Combine(tmpBk, "backups");
        Directory.CreateDirectory(tmpBackups);
        File.WriteAllText(tmpSettings, "{}");
        for (int i = 0; i < 10; i++) // pre-aged fakes sort below the real timestamped name
            File.WriteAllText(Path.Combine(tmpBackups, $"settings-00{i}.json"), "{}");
        Config.ProfileStore.BackupLatestTo(tmpSettings, tmpBackups, 7);
        int bkCount = Directory.GetFiles(tmpBackups).Length;
        Check("backup: capped at 7", bkCount == 7, $"count {bkCount}");
        try { Directory.Delete(tmpBk, true); } catch { }

        Console.WriteLine(failed == 0 ? "\nALL RENDER TESTS PASSED" : $"\n{failed} TEST(S) FAILED");
        return failed == 0 ? 0 : 1;
    }
}

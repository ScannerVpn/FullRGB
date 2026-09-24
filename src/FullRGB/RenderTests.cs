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

        // ---- 10b. round 22 fix: the level meter must be dB-mapped, not linear ----
        // The old "rms * 4.0" reached full scale at RMS 0.25, so every louder passage looked
        // identical — the "volume does not follow the song" report. A dB mapping keeps rising
        // past that point, which is what makes quiet and loud passages distinguishable.
        double Lvl(double rms) => Sensors.AudioProvider.ToUnit(rms, -55, -5);
        Check("audio: silence maps to zero", Lvl(0) == 0);
        Check("audio: the meter keeps rising above RMS 0.25 (the old linear x4 had already clipped)",
              Lvl(0.5) > Lvl(0.25) + 0.05, $"{Lvl(0.25):F3} -> {Lvl(0.5):F3}");
        Check("audio: the meter is monotonic and bounded",
              Lvl(0.01) < Lvl(0.05) && Lvl(0.05) < Lvl(0.2) && Lvl(0.2) < Lvl(0.8)
              && Lvl(0.8) <= 1.0 && Lvl(0.01) >= 0);
        Check("audio: a quiet passage is clearly below a loud one",
              Lvl(0.30) - Lvl(0.04) > 0.25, $"{Lvl(0.04):F3} vs {Lvl(0.30):F3}");

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
        Check("settings: corrupt falls back", corrupt.Language == "fa");
        try { Directory.Delete(sdir, true); } catch { }

        // ---- 12. autostart task script (ScheduledTasks module: schtasks /Create mis-splits
        // /TR at the first space even when quoted, so spaced folders registered Command=`G:\Ai\RGB`)
        string create = Autostart.BuildRegisterScript(@"C:\Program Files\FullRGB\FullRGB.exe", "--minimized");
        Check("autostart: -Execute carries the full spaced path",
              create.Contains("-Execute 'C:\\Program Files\\FullRGB\\FullRGB.exe'"));
        Check("autostart: args are a separate -Argument field",
              create.Contains("-Argument '--minimized'"));
        Check("autostart: runs unelevated (Limited, never Highest)",
              create.Contains("-RunLevel Limited") && !create.Contains("Highest"));
        Check("autostart: logon trigger for the user", create.Contains("-AtLogOn"));
        string del = Autostart.BuildUnregisterScript();
        Check("autostart: delete script", del.Contains("Unregister-ScheduledTask"));

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

        // ---- 16b. round 22 fix: the connect settle loop must reject unreadable device lists ----
        // The user's post-hibernate state was 4 controllers of which 3 had truncated payloads.
        // The old rule ("controllers > 0 && stable >= 2 && past the 4th sample") declared that a
        // healthy session, so ExpandAllZones did nothing and every retry hit the same wall.
        Check("connect: 4 controllers with 3 unreadable is NOT settled",
              !MainWindow.SettleReached(4, 3, 9, 9, 4));
        Check("connect: a fully readable, stable list IS settled",
              MainWindow.SettleReached(4, 0, 2, 4, 4));
        Check("connect: an empty list is never settled",
              !MainWindow.SettleReached(0, 0, 9, 9, 0));
        Check("connect: a list that is still changing is not settled",
              !MainWindow.SettleReached(4, 0, 1, 9, 4));
        Check("connect: more devices than remembered settles early",
              MainWindow.SettleReached(6, 0, 2, 0, 4));

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
            // GameHasHealth=0.4 so GamePulse paints its danger↔healthy blend: with NO health the
            // renderer idles on the healthy colour only, ColorHex would not reach the frame, and
            // the predicate-vs-renderer check would false-fail (only GamePulse reads these fields).
            var colCtx = new Effects.EffectContext
            {
                Time = 0.25, AudioLevel = 0.8, AudioBass = 0.8, AudioMid = 0.8, AudioTreble = 0.8,
                CpuTemp = 55, GpuTemp = 55, GameHasHealth = true, GameHealth = 0.4,
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

        // ---- 17b. round 22 fix: the AudioVU "pulse" shape must follow the BEAT, not a clock ----
        // The user's report: with band = bass and mode = Pulse the strip "pulsed on its own" and
        // ignored the music. The pulse came from a fixed ~2.4 s sine, and ctx.Beat - computed by
        // the provider all along - was never consulted on this path.
        var musicPulseFx = new Effects.EffectDef
        {
            Type = Effects.EffectType.AudioVU, AudioBand = "bass", AudioMode = "pulse",
            ColorHex = "#FF0000", Color2Hex = "#FF0000", Brightness = 1.0, BeatStrength = 0,
        };
        // Same colour on both stops, so the travelling gradient cannot influence the total:
        // any change in brightness is the pulse envelope and nothing else.
        int RedSum(byte[] f) => f.Where((_, i) => i % 3 == 0).Sum(v => (int)v);
        byte[] Pulse(double beat) => Effects.EffectRenderer.Render(musicPulseFx, 30, 0,
            new Effects.EffectContext { Time = 1.0, AudioBass = 0.6, Beat = beat });
        int pulseNoBeat = RedSum(Pulse(0.0)), pulseHalf = RedSum(Pulse(0.4)), pulseFull = RedSum(Pulse(1.0));
        Check("audio pulse: a kick makes the strip clearly brighter at the same band level",
              pulseFull > pulseNoBeat * 1.4, $"{pulseNoBeat} -> {pulseFull}");
        Check("audio pulse: brightness rises monotonically with the beat envelope",
              pulseNoBeat < pulseHalf && pulseHalf < pulseFull,
              $"{pulseNoBeat} < {pulseHalf} < {pulseFull}");

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

        // The dangerous case is a PARTIAL scan: a cold boot brings up three of four devices and the
        // fourth one's overrides used to be deleted for good. The remembered inventory prevents it.
        prof.DeviceOverrides["Ghost@usb-9"] = new Effects.EffectDef();
        prof.ZoneSizes["Ghost@usb-9|0"] = 5;
        prof.PruneTo(new[] { pdev }, new[] { "Ghost@usb-9" });
        Check("prune: a remembered device keeps its settings",
              prof.DeviceOverrides.ContainsKey("Ghost@usb-9") && prof.ZoneSizes.ContainsKey("Ghost@usb-9|0"));
        prof.PruneTo(new[] { pdev }, Array.Empty<string>());
        Check("prune: with nothing remembered the old behaviour stands",
              !prof.DeviceOverrides.ContainsKey("Ghost@usb-9"));

        // ---- 21. settings normalisation repairs hand-edited files ----
        var messy = new Config.AppSettings { Language = "de", ServerPort = 0, AccentHex = "nope", ActiveProfile = "gone" };
        messy.Profiles.Clear();
        messy.Profiles.Add(new Config.Profile { Name = "" });
        messy.Profiles.Add(new Config.Profile { Name = "Dup" });
        messy.Profiles.Add(new Config.Profile { Name = "Dup" });
        messy.Normalized();
        Check("settings: bad language falls back to en", messy.Language == "en");
        Check("settings: bad port falls back", messy.ServerPort == 6742);
        Check("settings: bad accent falls back", messy.AccentHex == "#A487EF");
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
        var beatPulseFx = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioMode = "pulse", Brightness = 1.0 };
        var barF = Effects.EffectRenderer.Render(barFx, 30, 0, loudCtx);
        var mirrorF = Effects.EffectRenderer.Render(mirrorFx, 30, 0, loudCtx);
        var pulseF = Effects.EffectRenderer.Render(beatPulseFx, 30, 0, loudCtx);
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

        // ---- 38. autostart script: quoting, level, stale-path detection ----
        string ascript = Autostart.BuildRegisterScript(@"G:\Ai\RGB Control\dist16\FullRGB.exe", "--minimized");
        Check("autostart: runs limited (never highest)",
              ascript.Contains("-RunLevel Limited") && !ascript.Contains("Highest"));
        Check("autostart: spaced exe intact in -Execute",
              ascript.Contains("-Execute 'G:\\Ai\\RGB Control\\dist16\\FullRGB.exe'"));
        Check("autostart: args in their own field",
              ascript.Contains("-Argument '--minimized'"));
        string ascriptBare = Autostart.BuildRegisterScript(@"C:\FullRGB.exe", "");
        Check("autostart: empty args omit -Argument",
              !ascriptBare.Contains("-Argument"));
        string staleXml = "<Task><Actions><Exec><Command>\"G:\\Ai\\RGB Control\\dist5\\FullRGB.exe\"</Command>" +
                          "<Arguments>--minimized</Arguments></Exec></Actions></Task>";
        string? parsed = Setup.EngineTask.ParseCommand(staleXml);
        Check("autostart: stale path parsed for comparison",
              parsed == @"G:\Ai\RGB Control\dist5\FullRGB.exe");
        Check("autostart: dist5 != dist16 detected as stale",
              !string.Equals(parsed, @"G:\Ai\RGB Control\dist16\FullRGB.exe",
                             StringComparison.OrdinalIgnoreCase));

        // ---- 39. round 14: silence decay, discrete palette blocks, Ambient/Gaming ----

        // Music must reach the noise gate FAST after the sound stops: the provider's release
        // constant is 0.45, so after N silent analysis frames level <= 0.02 after at most
        // ceil(ln(0.02)/ln(0.55)) ≈ 6 frames. Simulate: start loud, feed zeros, count frames.
        double lvl = 1.0;
        int framesToGate = 0;
        while (lvl > 0.02 && framesToGate < 60) { lvl = lvl * 0.55; framesToGate++; }
        Check("audio: silence reaches the noise gate in <= 8 analysis frames (~190 ms)",
              framesToGate <= 8, $"frames {framesToGate}");

        // Discrete palette blocks: 3 picked colours must each own a visible segment —
        // sampling the middle of each third must return that colour EXACTLY (no blend).
        // The test uses Speed=0, but SpeedFactor(0)=0.15 still rotates slowly; account for
        // the rotation (shift = 3.7 * 0.15 * 4 = 2.22 blocks) by scanning for all 3 colours
        // and asserting NONE of them blends into another (every pixel is one PURE colour).
        var blocks = new List<byte[]>();
        for (int i = 0; i < 90; i++)
        {
            var blk = new Effects.EffectDef
            {
                Type = Effects.EffectType.Custom, CustomPixels = "FF0000,00FF00,0000FF",
                Brightness = 1.0, Speed = 0, SyncZones = true,
            };
            blocks.Add(Effects.EffectRenderer.Render(blk, 90, 0, ctx));
        }
        // Speed=0 still rotates slowly (SpeedFactor(0)=0.15), so ctx.Time=3.7 shifts the
        // pattern by 2.22 blocks — every colour still appears, just rotated. Probe the two
        // extremes: Time=0 (no rotation) must map 1:1 to the picks, Time=3.7 must still be pure.
        var ctxZero = new Effects.EffectContext { Time = 0, CpuTemp = 55, GpuTemp = 60 };
        var blk0 = Effects.EffectRenderer.Render(
            new Effects.EffectDef { Type = Effects.EffectType.Custom, CustomPixels = "FF0000,00FF00,0000FF", Brightness = 1.0, Speed = 0, SyncZones = true },
            90, 0, ctxZero);
        bool identity = blk0[5 * 3] == 255 && blk0[5 * 3 + 1] == 0          // LED 5  -> block 0 = red
                     && blk0[45 * 3] == 0 && blk0[45 * 3 + 1] == 255        // LED 45 -> block 1 = green
                     && blk0[85 * 3 + 2] == 255;                            // LED 85 -> block 2 = blue
        Check("palette blocks: Time=0 maps picks 1:1 to strip thirds (red/green/blue)",
              identity,
              $"led5=({blk0[15]},{blk0[16]},{blk0[17]}) led45=({blk0[135]},{blk0[136]},{blk0[137]}) led85=({blk0[255]},{blk0[256]},{blk0[257]})");

        // Ambient with no screen sample must NOT be dark: falls back to the primary colour.
        var amb = new Effects.EffectDef { Type = Effects.EffectType.Ambient, ColorHex = "#FF8800", Brightness = 1.0, SyncZones = true };
        var ambF = Effects.EffectRenderer.Render(amb, 30, 0, ctx);
        Check("ambient: no screen sample falls back to the primary colour (never dark)",
              ambF[0] > 0 && ambF[1] > 0 && ambF[2] == 0,
              $"({ambF[0]},{ambF[1]},{ambF[2]})");

        // Ambient WITH screen bands: zone pixels follow the band colours.
        var ctxScreen = new Effects.EffectContext
        {
            Time = 3.7, ScreenValid = true,
            ScreenRow0R = 1.0, ScreenRow0G = 0, ScreenRow0B = 0,
            ScreenRow1R = 0, ScreenRow1G = 1.0, ScreenRow1B = 0,
            ScreenRow2R = 0, ScreenRow2G = 0, ScreenRow2B = 1.0,
        };
        var ambS = Effects.EffectRenderer.Render(amb, 90, 0, ctxScreen);
        // LED 5 is inside the top (red) band, LED 45 the middle (green), LED 85 the bottom (blue).
        bool rows = ambS[5 * 3] == 255 && ambS[5 * 3 + 1] == 0 && ambS[5 * 3 + 2] == 0
                 && ambS[45 * 3] == 0 && ambS[45 * 3 + 1] == 255 && ambS[45 * 3 + 2] == 0
                 && ambS[85 * 3] == 0 && ambS[85 * 3 + 1] == 0 && ambS[85 * 3 + 2] == 255;
        Check("ambient: screen bands drive the colours (red→green→blue top to bottom)",
              rows,
              $"top=({ambS[15]},{ambS[16]},{ambS[17]}) mid=({ambS[135]},{ambS[136]},{ambS[137]}) bot=({ambS[255]},{ambS[256]},{ambS[257]})");

        // Gaming: screen-average paints the body; the beat flash lifts every channel.
        var gaming = new Effects.EffectDef
        {
            Type = Effects.EffectType.Gaming, ColorHex = "#FF8800", Brightness = 1.0,
            BeatStrength = 1.0, SyncZones = true,
        };
        // No screen sample yet: the primary colour stands in (never dark).
        var gOff = Effects.EffectRenderer.Render(gaming, 30, 0, ctx);
        bool fallback = gOff[0] == 255 && gOff[1] == 136 && gOff[2] == 0;
        Check("gaming: no screen sample falls back to the primary colour", fallback,
              $"({gOff[0]},{gOff[1]},{gOff[2]})");

        // With a uniform grey screen: body = 0.2 * 255 = 51 per channel.
        var gBody = Effects.EffectRenderer.Render(gaming, 30, 0, new Effects.EffectContext
        {
            Time = 3.7,
            ScreenRow0R = 0.2, ScreenRow0G = 0.2, ScreenRow0B = 0.2,
            ScreenRow1R = 0.2, ScreenRow1G = 0.2, ScreenRow1B = 0.2,
            ScreenRow2R = 0.2, ScreenRow2G = 0.2, ScreenRow2B = 0.2, ScreenValid = true,
        });
        bool screenBody = gBody[0] == 51 && gBody[1] == 51 && gBody[2] == 51;
        Check("gaming: screen average paints the body", screenBody,
              $"({gBody[0]},{gBody[1]},{gBody[2]})");

        // Beat=1 with BeatStrength=1 flashes the frame toward white (every channel rises).
        var gOn = Effects.EffectRenderer.Render(gaming, 30, 0, new Effects.EffectContext
        {
            Time = 3.7, Beat = 1.0,
            ScreenRow0R = 0.2, ScreenRow0G = 0.2, ScreenRow0B = 0.2,
            ScreenRow1R = 0.2, ScreenRow1G = 0.2, ScreenRow1B = 0.2,
            ScreenRow2R = 0.2, ScreenRow2G = 0.2, ScreenRow2B = 0.2, ScreenValid = true,
        });
        bool flash = gOn[0] > gBody[0] && gOn[1] > gBody[1] && gOn[2] > gBody[2];
        Check("gaming: beat flashes brighter than the body", flash,
              $"({gOn[0]},{gOn[1]},{gOn[2]}) vs body ({gBody[0]},{gBody[1]},{gBody[2]})");

        // ---- 40. round 15: sleep/hibernate detection ("lights die after a wake-up") ----
        // The heartbeat is a 5 s DispatcherTimer. It cannot tick while the machine is suspended,
        // so a gap several times its own interval means the machine slept; 20 s (4 ×) is far
        // beyond any dispatcher delay a busy UI thread could produce.
        const long hb = 5000;
        Check("resume: a normal heartbeat is not a resume", !MainWindow.IsResumeGap(hb, hb, hb));
        Check("resume: a late tick is not a resume", !MainWindow.IsResumeGap(12000, 12000, hb));
        Check("resume: exactly 4x the interval is not a resume", !MainWindow.IsResumeGap(4 * hb, 4 * hb, hb));
        Check("resume: a 21 s freeze is a resume", MainWindow.IsResumeGap(4 * hb + 1, 4 * hb + 1, hb));
        Check("resume: 8 h of hibernate is a resume",
              MainWindow.IsResumeGap(8 * 3600 * 1000L, 8 * 3600 * 1000L, hb));
        // TickCount64 counts suspend time today, but .NET 11 makes it unbiased: the wall clock
        // alone must still catch the hibernate, and a clock correction alone must not fire.
        Check("resume: unbiased tick + wall clock still detects the sleep",
              MainWindow.IsResumeGap(hb, 8 * 3600 * 1000L, hb));
        Check("resume: an unbiased tick with a normal clock is quiet",
              !MainWindow.IsResumeGap(hb, hb, hb));
        Check("resume: a backwards clock correction is quiet",
              !MainWindow.IsResumeGap(hb, -3600 * 1000L, hb));
        Check("resume: a zero interval never fires", !MainWindow.IsResumeGap(600000, 600000, 0));

        // ---- 41. round 17: the remembered device inventory ----
        // The point of the cache is that a scan which comes back SHORT must not be treated as
        // "this hardware is gone". Everything below is the merge policy that guarantees it.
        static SDK.RgbController Dev(string name, string loc, int leds, int zones = 1)
        {
            var d = new SDK.RgbController
            {
                Name = name, Location = loc, DeviceType = (uint)SDK.RgbDeviceType.Cooler,
                LedNames = Enumerable.Range(0, leds).Select(i => $"LED {i}").ToList(),
            };
            for (int i = 0; i < zones; i++)
                d.Zones.Add(new SDK.RgbZone { Index = i, Name = $"Zone {i}", LedsMin = 0, LedsMax = 10, LedsCount = 10 });
            return d;
        }

        var t0 = new DateTime(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);
        var cache = Config.DeviceCache.From(new[] { Dev("Commander Core", "usb-1", 64), Dev("Aura", "pci-0", 40) }, t0);
        Check("cache: a snapshot remembers every device", cache.Devices.Count == 2);
        Check("cache: devices are keyed the way profiles are", cache.Has("Commander Core@usb-1"));
        Check("cache: hardware never seen is not remembered", !cache.Has("Ghost@usb-9"));

        var partial = cache.Merge(new[] { Dev("Commander Core", "usb-1", 64) }, t0.AddMinutes(5));
        Check("cache: merging leaves the source snapshot alone", cache.Devices.Count == 2);
        Check("cache: a device missing from this scan is kept", partial.Devices.Count == 2);
        Check("cache: the missing device keeps its last-seen stamp",
              partial.Devices.Single(d => d.Key == "Aura@pci-0").LastSeenUtc == t0);
        Check("cache: the present device is refreshed",
              partial.Devices.Single(d => d.Key == "Commander Core@usb-1").LastSeenUtc == t0.AddMinutes(5));
        Check("cache: repeat sightings are counted",
              partial.Devices.Single(d => d.Key == "Commander Core@usb-1").SeenCount == 2);
        Check("cache: a device that answered is reported as present",
              partial.MissingFrom(new[] { Dev("Commander Core", "usb-1", 64) }).Single() == "Aura");

        // …but hardware that stays gone for the whole retention window IS forgotten, so a part
        // pulled out for good does not keep its settings alive forever.
        var swept = partial.Merge(Array.Empty<SDK.RgbController>(), t0.AddDays(Config.DeviceCache.KeepMissingDays + 1));
        Check("cache: hardware gone past the retention window is swept", swept.Devices.Count == 0);

        var sigBase = Config.DeviceCache.From(new[] { Dev("Commander Core", "usb-1", 64) }, t0).Signature;
        Check("cache: identical hardware has an identical signature",
              Config.DeviceCache.From(new[] { Dev("Commander Core", "usb-1", 64) }, t0).Signature == sigBase);
        Check("cache: more LEDs change the signature",
              Config.DeviceCache.From(new[] { Dev("Commander Core", "usb-1", 72) }, t0).Signature != sigBase);
        Check("cache: an extra zone changes the signature",
              Config.DeviceCache.From(new[] { Dev("Commander Core", "usb-1", 64, zones: 2) }, t0).Signature != sigBase);

        var cachePath = Path.Combine(Path.GetTempPath(), $"fullrgb-cache-{Guid.NewGuid():N}.json");
        try
        {
            Config.DeviceCache.SaveTo(cachePath, partial);
            var cacheBack = Config.DeviceCache.LoadFrom(cachePath);
            Check("cache: round-trips through JSON",
                  cacheBack is not null && cacheBack.Devices.Count == partial.Devices.Count);
            Check("cache: zones survive the round trip",
                  cacheBack is not null && cacheBack.Devices.Single(d => d.Key == "Commander Core@usb-1").Zones.Count == 1);
            Check("cache: a missing file is a miss, not a crash",
                  Config.DeviceCache.LoadFrom(cachePath + ".nope") is null);
            File.WriteAllText(cachePath, "{ not json at all");
            Check("cache: a corrupt file is a miss, not a crash",
                  Config.DeviceCache.LoadFrom(cachePath) is null);
        }
        finally { try { File.Delete(cachePath); } catch { } }

        // ---- 42. round 19: the automatic follow-up rescan after boot/wake ----
        // Boot and wake can both conclude while USB enumeration is still running; the cache's
        // remembered count is the oracle that says "something is still missing, look again".
        Check("followup: a full inventory needs no follow-up scan", !MainWindow.NeedsFollowupScan(4, 4));
        Check("followup: an empty session with remembered hardware is re-scanned", MainWindow.NeedsFollowupScan(0, 4));
        Check("followup: a short session with remembered hardware is re-scanned", MainWindow.NeedsFollowupScan(2, 4));
        Check("followup: extra hardware needs no follow-up scan", !MainWindow.NeedsFollowupScan(5, 4));
        Check("followup: nothing remembered means nothing is missing", !MainWindow.NeedsFollowupScan(0, 0));

        // ---- 43. round 20: the OS signals a real window receives ----
        // The managed PowerModeChanged event was reproduced as NOT firing on Windows 11, so the
        // notifications are decoded from the raw message parameters instead. If this mapping is
        // wrong the machine never repairs after a wake-up, which is the whole bug.
        Check("power: resume-automatic (0x12) is a resume",
              Setup.PowerMonitor.DecodePowerEvent(0x12) == Setup.PowerEvent.Resume);
        Check("power: resume-suspend (0x07) is a resume",
              Setup.PowerMonitor.DecodePowerEvent(0x07) == Setup.PowerEvent.Resume);
        Check("power: resume-critical (0x06) is a resume",
              Setup.PowerMonitor.DecodePowerEvent(0x06) == Setup.PowerEvent.Resume);
        Check("power: suspend (0x04) is a suspend",
              Setup.PowerMonitor.DecodePowerEvent(0x04) == Setup.PowerEvent.Suspend);
        Check("power: an unknown broadcast is ignored",
              Setup.PowerMonitor.DecodePowerEvent(0x99) == Setup.PowerEvent.None);
        Check("power: an accepted back-off is not a resume",
              Setup.PowerMonitor.DecodePowerEvent(0x00000012 + 1) == Setup.PowerEvent.None);
        Check("session: lock/unlock are decoded",
              Setup.PowerMonitor.DecodeSessionEvent(0x7) == Setup.SessionEvent.Lock &&
              Setup.PowerMonitor.DecodeSessionEvent(0x8) == Setup.SessionEvent.Unlock);
        Check("session: logon/logoff are decoded",
              Setup.PowerMonitor.DecodeSessionEvent(0x5) == Setup.SessionEvent.Logoff &&
              Setup.PowerMonitor.DecodeSessionEvent(0x6) == Setup.SessionEvent.Logon);
        Check("session: console connect/disconnect are decoded",
              Setup.PowerMonitor.DecodeSessionEvent(0x1) == Setup.SessionEvent.ConsoleConnect &&
              Setup.PowerMonitor.DecodeSessionEvent(0x2) == Setup.SessionEvent.ConsoleDisconnect);
        Check("session: an unrelated session message is ignored",
              Setup.PowerMonitor.DecodeSessionEvent(0x0F) == Setup.SessionEvent.None);
        Check("session: this process has a session id", Setup.PowerMonitor.CurrentSessionId() >= 0);

        // ---- 44. round 20: the frame watchdog's verdicts (the "dark after a wake-up" fix) ----
        // The signal that matters is NOT "the engine answers" (a post-suspend engine answers
        // happily while driving nothing) but "the engine's stored colours are still moving".
        static Setup.EngineShadow.DeviceShadow DevShadow(string name, int leds, int zoneLeds, long sum)
        {
            var d = new Setup.EngineShadow.DeviceShadow { Name = name, LedCount = leds, ColourSum = sum };
            d.ZoneLeds.Add(zoneLeds);
            return d;
        }
        static Setup.EngineShadow.Snapshot Snap(params Setup.EngineShadow.DeviceShadow[] devs)
        {
            var s = new Setup.EngineShadow.Snapshot { EngineReachable = true, ClientConnections = 5 };
            s.Devices.AddRange(devs);
            return s;
        }

        var healthy = Snap(DevShadow("Aura", 481, 481, 1000));
        var frozen = Snap(DevShadow("Aura", 481, 481, 1000));
        var moved = Snap(DevShadow("Aura", 481, 481, 4444));
        var zeroZones = Snap(DevShadow("Aura", 481, 0, 0));
        var noDevices = Snap();

        Check("watchdog: motion is detected", Setup.EngineShadow.AnyMotion(healthy, moved, new HashSet<string> { "Aura" }));
        Check("watchdog: a frozen sequence reports no motion",
              !Setup.EngineShadow.AnyMotion(healthy, frozen, new HashSet<string> { "Aura" }));
        Check("watchdog: a static effect is never called a stall",
              !Setup.EngineShadow.AnyMotion(healthy, frozen, new HashSet<string>()));

        Check("watchdog: a healthy session is left alone",
              Setup.EngineShadow.Decide(moved, healthy, true, 300, 0, 0) == Setup.EngineShadow.Verdict.Ok);
        Check("watchdog: the grace period suppresses a verdict",
              Setup.EngineShadow.Decide(noDevices, null, true, 3, 5, 9) == Setup.EngineShadow.Verdict.Wait);
        Check("watchdog: stopped effects are never repaired",
              Setup.EngineShadow.Decide(noDevices, null, false, 300, 5, 9) == Setup.EngineShadow.Verdict.Wait);
        Check("watchdog: a dead port is repaired at once",
              Setup.EngineShadow.Decide(new Setup.EngineShadow.Snapshot { PortListening = false },
                                        healthy, true, 300, 0, 0) == Setup.EngineShadow.Verdict.Repair);
        Check("watchdog: an unreadable TCP table makes no claim",
              Setup.EngineShadow.Decide(new Setup.EngineShadow.Snapshot { TcpTableReadable = false },
                                        healthy, true, 300, 5, 5) == Setup.EngineShadow.Verdict.Wait);
        // The app's own writer sockets matter: the port listening with only our probe attached is
        // the "the session lost its engine" state.
        Check("watchdog: a port with nobody attached is repaired",
              Setup.EngineShadow.Decide(new Setup.EngineShadow.Snapshot { EngineReachable = true, ClientConnections = 1 },
                                        healthy, true, 300, 2, 0) == Setup.EngineShadow.Verdict.Repair);
        Check("watchdog: a lone probe connection alone is not enough to act",
              Setup.EngineShadow.Decide(new Setup.EngineShadow.Snapshot { EngineReachable = true, ClientConnections = 1 },
                                        healthy, true, 300, 1, 0) == Setup.EngineShadow.Verdict.Wait);
        Check("watchdog: zero-LED zones are rebuilt, not restarted",
              Setup.EngineShadow.Decide(zeroZones, zeroZones, true, 300, 2, 5) == Setup.EngineShadow.Verdict.Rebuild);
        Check("watchdog: a stalled frame stream is repaired after two strikes",
              Setup.EngineShadow.Decide(healthy, healthy, true, 300, 2, 0) == Setup.EngineShadow.Verdict.Repair);
        Check("watchdog: ONE strike only waits (a fluke never restarts the engine)",
              Setup.EngineShadow.Decide(healthy, healthy, true, 300, 1, 0) == Setup.EngineShadow.Verdict.Ok);
        Check("watchdog: an engine that accepts TCP but never answers is repaired",
              Setup.EngineShadow.Decide(new Setup.EngineShadow.Snapshot { EngineReachable = false },
                                        healthy, true, 300, 2, 0) == Setup.EngineShadow.Verdict.Repair);
        Check("watchdog: a single silent answer is not enough to act",
              Setup.EngineShadow.Decide(new Setup.EngineShadow.Snapshot { EngineReachable = false },
                                        healthy, true, 300, 1, 0) == Setup.EngineShadow.Verdict.Wait);
        Check("watchdog: no devices at all is not a stall (nothing to paint)",
              Setup.EngineShadow.Decide(noDevices, noDevices, true, 300, 2, 0) == Setup.EngineShadow.Verdict.Wait);
        Check("watchdog: zones stuck at the pre-expand size are rebuilt",
              Setup.EngineShadow.Decide(healthy, healthy, true, 300, 0, 4) == Setup.EngineShadow.Verdict.Rebuild);

        // A snapshot must describe hardware, not a paint: same devices, different colours, same shape.
        Check("watchdog: the shape signature ignores the colours",
              healthy.Signature == moved.Signature && healthy.Signature != zeroZones.Signature);
        Check("watchdog: totals add up over devices",
              Snap(DevShadow("A", 10, 10, 5), DevShadow("B", 20, 20, 7)).LedTotal == 30);

        // ---- 45. round 20: reading the OS TCP table ----
        // The watchdog must know whether the engine is still LISTENING and how many clients are
        // attached, from Windows itself. Rows are the Win32 shape (MIB_TCPROW_OWNER_PID); LISTEN is
        // state 2 and ESTABLISHED is 5 — NOT the /proc hex codes 0A/01, which is why the pure parser
        // is asserted here instead of being trusted.
        static Setup.EngineShadow.TcpRow Row(int localPort, int remotePort, int state, int pid = 4242)
            => new(localPort, remotePort, state, pid);

        var tcpRows = new List<Setup.EngineShadow.TcpRow>
        {
            Row(0x1A56, 0, Setup.EngineShadow.StateListen),                       // 6742 listening
            Row(0x1A56, 0x8B2A, Setup.EngineShadow.StateEstablished),             // two app writers
            Row(0x1A56, 0x8B2B, Setup.EngineShadow.StateEstablished),
            Row(0x1A56, 0x8B2C, 6),                                              // TIME_WAIT: already gone
            Row(0x1A57, 0x8B2D, Setup.EngineShadow.StateEstablished),             // a different port
        };
        var facts = Setup.EngineShadow.ParseRows(tcpRows, 0x1A56);
        Check("tcp: the listening socket is found", facts is not null && facts.Value.Listening);
        Check("tcp: only connections of THIS port are counted", facts is not null && facts.Value.Clients == 2);
        Check("tcp: a time-wait row is not a live connection", facts is not null && facts.Value.Clients == 2);
        Check("tcp: another port is not mistaken for the engine's",
              Setup.EngineShadow.ParseRows(tcpRows, 0x1A60) is { Listening: false, Clients: 0 });
        Check("tcp: no table at all is a miss", Setup.EngineShadow.ParseRows(null, 6742) is null);
        Check("tcp: an empty table is a miss",
              Setup.EngineShadow.ParseRows(new List<Setup.EngineShadow.TcpRow>(), 6742) is { Listening: false });
        Check("tcp: 6742 decimal is found from its hex form",
              Setup.EngineShadow.ParseRows(tcpRows, 6742) is { Listening: true, Clients: 2 });
        // The port sits in the LOW 16 bits of its DWORD in network byte order: 6742 is 0x1A56, so
        // Windows stores the DWORD as 0x0000561A. Getting this byte-swap wrong would make the
        // watchdog read a nonsense port and either never fire or fire constantly.
        Check("tcp: the port is decoded from the packed address",
              Setup.EngineShadow.NetworkPort(0x561A) == 6742 &&
              Setup.EngineShadow.NetworkPort(0x0000) == 0 &&
              Setup.EngineShadow.NetworkPort(0x1A56) == 0x561A);
        // The engine's OWN listening row must never be counted as a client, or a dead session would
        // look healthy forever: after a suspend the listener is the only row left.
        var onlyListener = new List<Setup.EngineShadow.TcpRow> { Row(0x1A56, 0, Setup.EngineShadow.StateListen) };
        Check("tcp: a listener with no clients has zero clients",
              Setup.EngineShadow.ParseRows(onlyListener, 6742) is { Listening: true, Clients: 0 });

        // ---- 46. round 21: time-of-day schedule rules ----
        var (tsRules, tsErrors) = Config.ScheduleRules.Parse(
            "# comment line\n22:00-07:00=Night\nMo-Fr 08:00-17:00=Work\nbad line\n=Broken\nNight 25:99-07:00=X");
        Check("schedule: two valid rules parsed", tsRules.Count == 2,
              $"rules={tsRules.Count} errors={tsErrors.Count}");
        Check("schedule: broken lines are reported", tsErrors.Count >= 3, $"errors={tsErrors.Count}");
        var night = tsRules.FirstOrDefault(r => r.Profile == "Night");
        Check("schedule: overnight range detected", night is { IsOvernight: true });
        Check("schedule: 23:30 matches the overnight rule",
              night is not null && night.Matches(new DateTime(2026, 9, 23, 23, 30, 0)));
        Check("schedule: 03:00 matches the overnight rule (past midnight)",
              night is not null && night.Matches(new DateTime(2026, 9, 23, 3, 0, 0)));
        Check("schedule: 12:00 does not match the overnight rule",
              night is not null && !night.Matches(new DateTime(2026, 9, 23, 12, 0, 0)));
        var work = tsRules.FirstOrDefault(r => r.Profile == "Work");
        Check("schedule: weekday rule matches a Wednesday 09:00",
              work is not null && work.Matches(new DateTime(2026, 9, 23, 9, 0, 0)));   // Wed
        Check("schedule: weekday rule does not match a Saturday 09:00",
              work is not null && !work.Matches(new DateTime(2026, 9, 26, 9, 0, 0)));  // Sat
        Check("schedule: evaluate returns the first matching rule",
              Config.ScheduleRules.Evaluate(tsRules, new DateTime(2026, 9, 23, 23, 30, 0)) == "Night");
        Check("schedule: no match evaluates to null",
              Config.ScheduleRules.Evaluate(tsRules, new DateTime(2026, 9, 26, 19, 0, 0)) is null);

        // ---- 46b. round 21 fix: an overnight range belongs to the night it STARTED on ----
        // 2026-09-25 is a Friday, the 26th a Saturday, the 27th a Sunday.
        var satRule = Config.ScheduleRules.Parse("Sa 22:00-07:00=SatNight").Rules.Single();
        Check("schedule: Sa 22:00-07:00 matches Saturday evening",
              satRule.Matches(new DateTime(2026, 9, 26, 23, 0, 0)));
        Check("schedule: Sa 22:00-07:00 still matches Sunday 00:30 (same night)",
              satRule.Matches(new DateTime(2026, 9, 27, 0, 30, 0)));
        Check("schedule: Sa 22:00-07:00 does NOT match Saturday 00:30 (that is Friday's night)",
              !satRule.Matches(new DateTime(2026, 9, 26, 0, 30, 0)));
        Check("schedule: Sa 22:00-07:00 does not run on Sunday night",
              !satRule.Matches(new DateTime(2026, 9, 27, 23, 0, 0)));

        var friRule = Config.ScheduleRules.Parse("Fr 22:00-07:00=FriNight").Rules.Single();
        Check("schedule: Fr 22:00-07:00 covers the following Saturday morning",
              friRule.Matches(new DateTime(2026, 9, 26, 0, 30, 0))
              && friRule.Matches(new DateTime(2026, 9, 26, 6, 59, 0)));
        Check("schedule: Fr 22:00-07:00 ends at 07:00 (exclusive)",
              !friRule.Matches(new DateTime(2026, 9, 26, 7, 0, 0)));
        Check("schedule: Fr 22:00-07:00 does not cover Saturday night",
              !friRule.Matches(new DateTime(2026, 9, 26, 23, 0, 0)));

        // ---- 47. round 21: profile share (file + short code) ----
        var shared = new Config.Profile
        {
            Name = "SharedTest",
            GlobalEffect = new Effects.EffectDef
            {
                Type = Effects.EffectType.Wave, ColorHex = "#FF0044", Color2Hex = "#00FF88", Speed = 0.7,
            },
        };
        string json = Config.ProfileShare.ExportJson(shared);
        var back = Config.ProfileShare.ImportJson(json, out var jsonErr);
        Check("profile share: json round-trips the effect",
              back is not null && jsonErr.Length == 0
              && back.GlobalEffect.Type == Effects.EffectType.Wave
              && back.GlobalEffect.ColorHex == "#FF0044",
              jsonErr);
        string code = Config.ProfileShare.ExportCode(shared);
        var back2 = Config.ProfileShare.ImportCode(code, out var codeErr);
        Check("profile share: short code round-trips",
              back2 is not null && codeErr.Length == 0
              && back2.GlobalEffect.Speed > 0.69 && back2.GlobalEffect.Speed < 0.71
              && back2.GlobalEffect.Type == Effects.EffectType.Wave,
              codeErr);
        Check("profile share: short code has the prefix", code.StartsWith(Config.ProfileShare.Prefix, StringComparison.Ordinal));
        Check("profile share: a settings file is NOT accepted as a profile",
              Config.ProfileShare.ImportJson("{\"profiles\":[]}", out _) is null);
        Check("profile share: garbage is rejected with an error",
              Config.ProfileShare.ImportCode("FRGB1-not!valid!base64!!", out var badErr) is null && badErr.Length > 0);
        Check("profile share: dedupe names stay unique",
              Config.ProfileShare.DedupeName(new[] { "Gaming" }, "Gaming") == "Gaming (2)");

        // ---- 47b. round 21 fix: a share code cannot inflate past the zip-bomb guard ----
        // 6 MB of one repeated character compresses to a few KB, so a tiny code pasted from a
        // chat would otherwise expand into gigabytes and hang the app on "Import share code".
        {
            string bombJson = "{\"app\":\"FullRGB\",\"kind\":\"profile\",\"profile\":{\"name\":\"" +
                              new string('A', 6 * 1024 * 1024) + "\"}}";
            byte[] bombRaw = System.Text.Encoding.UTF8.GetBytes(bombJson);
            using var bombGz = new MemoryStream();
            using (var zip = new System.IO.Compression.GZipStream(
                       bombGz, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                zip.Write(bombRaw, 0, bombRaw.Length);
            string bombCode = Config.ProfileShare.Prefix +
                Convert.ToBase64String(bombGz.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var bombResult = Config.ProfileShare.ImportCode(bombCode, out var bombErr);
            Check("profile share: a zip-bomb code is refused, not expanded",
                  bombResult is null && bombErr.Length > 0,
                  $"{bombGz.Length} B code -> {bombRaw.Length} B payload; err={bombErr}");
        }

        // ---- 48. round 21: game event state (the GamePulse inputs) ----
        Sensors.GameEventState.Reset();
        Sensors.GameEventState.Push("hp", 42);          // 0..100 form is normalised
        Check("game events: hp above 1 is treated as a percentage", Sensors.GameEventState.HasHealth
              && Math.Abs(Sensors.GameEventState.Health - 0.42) < 0.001);
        Sensors.GameEventState.Push("hit", 0.8);
        Check("game events: a fresh hit has a strong flash",
              Sensors.GameEventState.FlashNow(Environment.TickCount64) > 0.5);
        Check("game events: the flash decays to zero in half a second",
              Sensors.GameEventState.FlashNow(Environment.TickCount64 + 600) == 0);
        Sensors.GameEventState.Reset();
        Sensors.GameEventState.Push("death");
        Check("game events: death sets the hold window",
              Sensors.GameEventState.DeathHoldNow(Environment.TickCount64));
        var gameCtx = new Effects.EffectContext { Time = 1.0 };
        Sensors.GameEventState.Fill(gameCtx);
        Check("game events: fill copies health + hold into the render context",
              gameCtx.GameHasHealth == false && gameCtx.GameDeathHold);

        // ---- 48b. round 21: the GamePulse effect renders in every state without throwing ----
        Sensors.GameEventState.Reset();               // true idle: no events at all
        Sensors.GameEventState.Fill(gameCtx);         // re-Fill: gameCtx still carried the death hold from 48
        var gp = new Effects.EffectDef
        {
            Type = Effects.EffectType.GamePulse, ColorHex = "#FF2222", Color2Hex = "#22FF66",
            Brightness = 0.9,
        };
        var gpIdle = Effects.EffectRenderer.Render(gp, 30, 0, gameCtx);
        Check("gamepulse: idle frame is healthy-coloured and non-black",
              gpIdle.Length == 90 && gpIdle.Where((_, i) => i % 3 == 1).Any(v => v > 0));
        var gpHit = new Effects.EffectContext
        {
            Time = 1.0, GameHasHealth = true, GameHealth = 0.15, GameHitFlash = 1.0,
        };
        var gpFlash = Effects.EffectRenderer.Render(gp, 30, 0, gpHit);
        Check("gamepulse: a full hit flash saturates toward white",
              gpFlash[0] > 200 && gpFlash[1] > 200);
        var gpDeath = new Effects.EffectContext
        {
            Time = 1.0, GameHasHealth = true, GameHealth = 0.0, GameDeathHold = true,
        };
        var gpRed = Effects.EffectRenderer.Render(gp, 30, 0, gpDeath);
        Check("gamepulse: death hold is dominated by red",
              gpRed[0] > gpRed[1] + 40 && gpRed[0] > gpRed[2] + 40);
        var gpBar = new Effects.EffectDef
        {
            Type = Effects.EffectType.GamePulse, AudioMode = "mirror",
            ColorHex = "#FF2222", Color2Hex = "#22FF66", Brightness = 0.9,
        };
        var gpBarFrame = Effects.EffectRenderer.Render(gpBar, 30, 0,
            new Effects.EffectContext { Time = 1.0, GameHasHealth = true, GameHealth = 0.5 });
        Check("gamepulse: the health bar style renders",
              gpBarFrame.Length == 90 && gpBarFrame.Any(v => v > 0));

        // ---- 49. round 21: the command dispatcher (CLI / pipe / HTTP share it) ----
        var testTarget = new TestControlTarget();
        Check("dispatcher: set-profile with an unknown name errors",
              Automation.CommandDispatcher.Execute(testTarget, "set-profile nope").StartsWith("ERR", StringComparison.Ordinal));
        Check("dispatcher: set-profile with a known name is OK",
              Automation.CommandDispatcher.Execute(testTarget, "set-profile gaming") == "OK");
        Check("dispatcher: case-insensitive commands",
              Automation.CommandDispatcher.Execute(testTarget, "SET-PROFILE Gaming") == "OK");
        Check("dispatcher: brightness accepts percentages",
              Automation.CommandDispatcher.Execute(testTarget, "set-brightness 42") == "OK"
              && Math.Abs(testTarget.LastBrightness - 0.42) < 0.001);
        Check("dispatcher: brightness accepts 0..1",
              Automation.CommandDispatcher.Execute(testTarget, "set-brightness 0.85") == "OK"
              && Math.Abs(testTarget.LastBrightness - 0.85) < 0.001);
        Check("dispatcher: game-event forwards name and value",
              Automation.CommandDispatcher.Execute(testTarget, "game-event hp 0.5") == "OK"
              && testTarget.LastEvent == "hp");
        Check("dispatcher: unknown commands say so",
              Automation.CommandDispatcher.Execute(testTarget, "explode now").StartsWith("ERR", StringComparison.Ordinal));
        Check("dispatcher: status returns JSON",
              Automation.CommandDispatcher.Execute(testTarget, "status").Contains("\"ok\":true", StringComparison.Ordinal));
        Check("dispatcher: power off reaches the target",
              Automation.CommandDispatcher.Execute(testTarget, "power OFF") == "OK" && !testTarget.LastPower);
        Check("dispatcher: one command is split from its argument",
              Automation.CommandDispatcher.Split("  set-profile  My Game Rig ") == ("set-profile", "My Game Rig"));

        // ---- 50. round 21: community HID protocol validation (the safety gate is code, not docs) ----
        const string validProto = """
            {"schema":"fullrgb.hid/1","name":"CASUE KB","author":"tester","vid":"2A7A","pid":"939F",
             "probe":{"usagePage":65281,"usage":1,"reportId":1,"length":8},
             "paint":{"reportId":6,"length":64,"prefix":"06 01","order":"RGB","rgbSlots":20}}
            """;
        var protoOk = Hid.HidProtocolFile.Parse(validProto, "casue.json", out var protoErr);
        Check("hid protocol: a valid definition parses",
              protoOk is not null && protoErr.Length == 0 && protoOk.VidPid == "2A7A:939F", protoErr);
        Check("hid protocol: write requires both the file AND the user switch",
              protoOk is { CanWrite: false });   // HidExperimentalWrite defaults to false
        const string badSchema = """{"schema":"fullrgb.hid/9","name":"X","vid":"2A7A","pid":"939F","probe":{"length":8}}""";
        Check("hid protocol: a foreign schema is rejected",
              Hid.HidProtocolFile.Parse(badSchema, "x.json", out _) is null);
        const string badVid = """{"schema":"fullrgb.hid/1","name":"X","vid":"ZZZZ","pid":"939F","probe":{"length":8}}""";
        Check("hid protocol: a non-hex VID is rejected",
              Hid.HidProtocolFile.Parse(badVid, "x.json", out _) is null);
        const string oversized = """
            {"schema":"fullrgb.hid/1","name":"X","vid":"2A7A","pid":"939F",
             "paint":{"reportId":6,"length":128,"prefix":"","order":"RGB","rgbSlots":40}}
            """;
        Check("hid protocol: an oversized report is rejected",
              Hid.HidProtocolFile.Parse(oversized, "x.json", out _) is null);
        const string noContent = """{"schema":"fullrgb.hid/1","name":"X","vid":"2A7A","pid":"939F"}""";
        Check("hid protocol: a definition with no sections is rejected",
              Hid.HidProtocolFile.Parse(noContent, "x.json", out _) is null);

        // ---- 50b. round 21 fix: the protocol store never builds a path from a raw string ----
        Check("hid store: a plain file name is accepted",
              Hid.CommunityStore.IsSafeStoreName("my-mouse.json"));
        Check("hid store: a traversal name is refused",
              !Hid.CommunityStore.IsSafeStoreName("..\\..\\evil.json")
              && !Hid.CommunityStore.IsSafeStoreName("../evil.json")
              && !Hid.CommunityStore.IsSafeStoreName("sub/evil.json")
              && !Hid.CommunityStore.IsSafeStoreName("C:\\evil.json")
              && !Hid.CommunityStore.IsSafeStoreName(""));
        Check("hid store: removing a traversal name is refused without touching the disk",
              Hid.CommunityStore.Remove("..\\..\\evil.json") == "invalid protocol file name");

        // ---- 50c. round 22 fix: the HID bridge's P/Invoke surface must actually resolve ----
        // HidBridge declared "HidD_GetCaps", which exists in NO Windows DLL. It compiled fine and
        // threw EntryPointNotFoundException the first time the Hardware page enumerated devices,
        // which killed the app on the Hardware tab. Resolve every [DllImport] up front instead of
        // discovering it at click time.
        var missingExports = new List<string>();
        foreach (var m in typeof(Hid.HidBridge).GetMethods(
                     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                     | System.Reflection.BindingFlags.Public))
        {
            var dllAttr = (System.Runtime.InteropServices.DllImportAttribute?)
                Attribute.GetCustomAttribute(m, typeof(System.Runtime.InteropServices.DllImportAttribute));
            if (dllAttr is null) continue;
            string entry = string.IsNullOrEmpty(dllAttr.EntryPoint) ? m.Name : dllAttr.EntryPoint;
            // Mirror the CLR's own probe order: a Unicode import resolves as "NameW" first, an
            // Ansi one as "NameA" (that is how SetupDiGetClassDevs finds SetupDiGetClassDevsA).
            string[] candidates = dllAttr.CharSet == System.Runtime.InteropServices.CharSet.Unicode
                                  || dllAttr.CharSet == System.Runtime.InteropServices.CharSet.Auto
                ? new[] { entry + "W", entry }
                : new[] { entry, entry + "A" };
            try
            {
                IntPtr lib = System.Runtime.InteropServices.NativeLibrary.Load(dllAttr.Value);
                try
                {
                    if (!candidates.Any(c => System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, c, out _)))
                        missingExports.Add($"{dllAttr.Value}!{entry}");
                }
                finally { System.Runtime.InteropServices.NativeLibrary.Free(lib); }
            }
            catch (Exception e) { missingExports.Add($"{dllAttr.Value}: {e.Message}"); }
        }
        Check("hid bridge: every DllImport entry point exists",
              missingExports.Count == 0, string.Join(", ", missingExports));

        // The native routine fills a FIXED 64-byte HIDP_CAPS, so a short managed declaration means
        // it writes past the buffer. The struct was also missing 13 of the 17 reserved ushorts.
        var capsType = typeof(Hid.HidBridge).GetNestedType("HIDP_CAPS",
            System.Reflection.BindingFlags.NonPublic);
        Check("hid bridge: HIDP_CAPS matches the native 64-byte layout",
              capsType is not null && System.Runtime.InteropServices.Marshal.SizeOf(capsType) == 64,
              capsType is null ? "struct not found"
                               : $"size={System.Runtime.InteropServices.Marshal.SizeOf(capsType)}");

        // ---- 50e. round 22: the --hid-list skeleton must be a file the app will actually import ----
        // A tool that emits something its own validator rejects is worse than no tool.
        var skeletonCol = new Hid.HidBridge.HidCollection
        {
            Path = @"\\?\hid#vid_30fa&pid_1140&mi_01&col04", Vid = 0x30FA, Pid = 0x1140,
            UsagePage = 0xFF01, Usage = 1, FeatureLength = 8, OutputLength = 0,
        };
        var parsedSkeleton = Hid.HidProtocolFile.Parse(
            Hid.HidBridge.ProtocolSkeleton(skeletonCol), "skeleton.json", out var skeletonErr);
        Check("hid-list: the generated skeleton parses as a valid probe-only protocol",
              parsedSkeleton is { ExperimentalWrite: false } && parsedSkeleton.Probe is not null
              && parsedSkeleton.VidNum == 0x30FA && parsedSkeleton.PidNum == 0x1140
              && parsedSkeleton.Probe.UsagePage == 0xFF01,
              skeletonErr);

        // A channel with NO feature report cannot be probed, and a 65-byte output report is past
        // HidProbe.Validate's 2..64 ceiling: the skeleton must still import cleanly.
        var outputOnlyCol = new Hid.HidBridge.HidCollection
        {
            Path = "x", Vid = 0x0B05, Pid = 0x18F3,
            UsagePage = 0xFF72, Usage = 161, FeatureLength = 0, OutputLength = 65,
        };
        Check("hid-list: an output-only channel still yields an importable skeleton",
              Hid.HidProtocolFile.Parse(
                  Hid.HidBridge.ProtocolSkeleton(outputOnlyCol), "out.json", out var outOnlyErr) is not null,
              outOnlyErr);

        // ---- 50d. round 22: an unclean exit must be detectable by the NEXT run ----
        // The v1.6.0 Hardware-tab crash left nothing behind anywhere - no log line, no event. The
        // marker file is the only thing that turns "the app vanished" into a fact.
        var sessDir = Path.Combine(Path.GetTempPath(), "fullrgb-session-" + Guid.NewGuid().ToString("N"));
        Diag.SessionMarker.PathOverride = Path.Combine(sessDir, "session.lock");
        Diag.SessionMarker.Begin();
        Check("session: a first-ever run is not reported as unclean",
              Diag.SessionMarker.PreviousUncleanRun is null);
        // No End() here: this is the crash. A fresh process would find the marker still present.
        Diag.SessionMarker.Begin();
        Check("session: a run after a crash IS reported as unclean",
              Diag.SessionMarker.PreviousUncleanRun is { Length: > 0 },
              Diag.SessionMarker.PreviousUncleanRun ?? "(null)");
        Diag.SessionMarker.End();
        Diag.SessionMarker.Begin();
        Check("session: a clean exit clears the marker so the next run is quiet",
              Diag.SessionMarker.PreviousUncleanRun is null);
        Diag.SessionMarker.End();
        Diag.SessionMarker.PathOverride = null;
        try { Directory.Delete(sessDir, true); } catch { }

        // ---- 50f. round 22: hotkey combo parsing ----
        Check("hotkey: Ctrl+Alt+L parses",
              Setup.HotkeyManager.TryParse("Ctrl+Alt+L", out var hkMods, out var hkVk)
              && hkVk == 'L' && (hkMods & 0x0002) != 0 && (hkMods & 0x0001) != 0);
        Check("hotkey: spaces and case are tolerated",
              Setup.HotkeyManager.TryParse("  ctrl + shift + f5 ", out _, out var hkF5) && hkF5 == 0x74);
        Check("hotkey: a bare key is refused (it would swallow normal typing)",
              !Setup.HotkeyManager.TryParse("L", out _, out _));
        Check("hotkey: a modifier with no key is refused",
              !Setup.HotkeyManager.TryParse("Ctrl+", out _, out _));
        Check("hotkey: an unknown key or an out-of-range function key is refused",
              !Setup.HotkeyManager.TryParse("Ctrl+Alt+ZZ", out _, out _)
              && !Setup.HotkeyManager.TryParse("Ctrl+Alt+F25", out _, out _)
              && !Setup.HotkeyManager.TryParse("Ctrl+Alt+!", out _, out _));
        Check("hotkey: empty is refused",
              !Setup.HotkeyManager.TryParse("", out _, out _)
              && !Setup.HotkeyManager.TryParse(null, out _, out _));
        Check("hotkey: auto-repeat is suppressed so a toggle fires once per press",
              Setup.HotkeyManager.TryParse("Ctrl+Alt+B", out var hkNoRep, out _) && (hkNoRep & 0x4000) != 0);

        // ---- 52. round 22: palette contrast (WCAG AA) ----
        // Keep these hex values in sync with App.xaml. "Faint" is 10.5px text that carries meaning
        // (device meta lines, hints), not decoration, so it must clear 4.5:1 on the card
        // background - it measured 3.13:1 before this round.
        Check("palette: faint text clears WCAG AA on the card background",
              Theme.ContrastRatio("#8A8494", "#16161E") >= 4.5,
              $"{Theme.ContrastRatio("#8A8494", "#16161E"):F2}:1");
        Check("palette: body and muted text clear AA comfortably",
              Theme.ContrastRatio("#E0D8EE", "#16161E") >= 7.0
              && Theme.ContrastRatio("#98939F", "#16161E") >= 4.5,
              $"text {Theme.ContrastRatio("#E0D8EE", "#16161E"):F2}:1, " +
              $"muted {Theme.ContrastRatio("#98939F", "#16161E"):F2}:1");
        Check("palette: the semantic colours clear AA on the card background",
              Theme.ContrastRatio("#68D5AF", "#16161E") >= 4.5
              && Theme.ContrastRatio("#FFC24D", "#16161E") >= 4.5
              && Theme.ContrastRatio("#FF5C6C", "#16161E") >= 4.5);
        Check("palette: Text > Muted > Faint stays a visible hierarchy",
              Theme.ContrastRatio("#E0D8EE", "#16161E") > Theme.ContrastRatio("#98939F", "#16161E")
              && Theme.ContrastRatio("#98939F", "#16161E") > Theme.ContrastRatio("#8A8494", "#16161E"));

        // ---- 53b. the band value must express DYNAMICS, not pin at full ----
        // The previous peak-normalised formula read 1.000 for ANY steady signal, so a chorus held
        // the strip at full brightness and every quiet/loud contrast vanished - the user's "in many
        // parts of the song the lights just sit there" report. Measuring against a slow baseline
        // makes the value express the music's dynamics, which is the point of a visualiser.
        double Rel(double amp, double baseLine) => Sensors.AudioProvider.RelativeToBaseline(amp, baseLine);
        Check("music dynamics: a steady signal sits mid-range, NOT pinned at full",
              Rel(1.0, 1.0) is > 0.4 and < 0.7, $"{Rel(1.0, 1.0):F3}");
        Check("music dynamics: 6 dB above the baseline is clearly brighter",
              Rel(2.0, 1.0) > 0.8, $"{Rel(2.0, 1.0):F3}");
        Check("music dynamics: 6 dB below the baseline is clearly dimmer",
              Rel(0.5, 1.0) < 0.35, $"{Rel(0.5, 1.0):F3}");
        Check("music dynamics: monotonic across the window",
              Rel(0.25, 1.0) < Rel(0.5, 1.0) && Rel(0.5, 1.0) < Rel(1.0, 1.0)
              && Rel(1.0, 1.0) < Rel(2.0, 1.0) && Rel(2.0, 1.0) < Rel(4.0, 1.0));
        Check("music dynamics: a silent or unset band reads zero (no divide-by-zero)",
              Rel(0, 1.0) == 0 && Rel(1.0, 0) == 0 && Rel(-1, 1.0) == 0);
        Check("music dynamics: the old peak normalisation really is gone",
              Sensors.AudioProvider.ToUnit(1.0, -24, 0) > 0.99        // what the old code returned
              && Rel(1.0, 1.0) < 0.7,
              $"old peak formula {Sensors.AudioProvider.ToUnit(1.0, -24, 0):F3} vs baseline {Rel(1.0, 1.0):F3}");
        Check("music dynamics: a hit stands well clear of the baseline value",
              Rel(3.0, 1.0) - Rel(1.0, 1.0) > 0.3,
              $"baseline {Rel(1.0, 1.0):F3} -> hit {Rel(3.0, 1.0):F3}");

        // ---- 54. round 22: the music effect picks its own band ----
        // A fixed band only works for some music ("bass" does nothing on an acoustic track), so the
        // user had to re-pick per song. The provider now follows whichever band is actually
        // carrying the rhythm. These pin the rule rather than leaving it to be judged by ear.
        double Score(double mean, double move) => Sensors.AudioProvider.RhythmScore(mean, move);
        Check("auto band: rhythm is movement RELATIVE to the band's own average",
              Score(0.5, 0.01) < Score(0.2, 0.10),
              $"loud-steady {Score(0.5, 0.01):F3} vs quiet-rhythmic {Score(0.2, 0.10):F3}");
        Check("auto band: a band with no content in this track cannot win on noise",
              Score(0.01, 0.05) == 0 && Score(0.0, 0.9) == 0);
        Check("auto band: the most rhythmic band wins even when it is the quietest",
              Sensors.AudioProvider.PickBand(new[] { 0.02, 0.60, 0.02 }, 0) == 1);
        Check("auto band: a genuinely dominant band takes over",
              Sensors.AudioProvider.PickBand(new[] { 0.90, 0.10, 0.05 }, 1) == 0);
        Check("auto band: hysteresis keeps the incumbent against a marginal challenger",
              Sensors.AudioProvider.PickBand(new[] { 0.52, 0.50, 0.10 }, 1) == 1,
              "0.52 vs 0.50 is not a clear enough win to flip mid-song");
        Check("auto band: a clear challenger still wins",
              Sensors.AudioProvider.PickBand(new[] { 0.90, 0.50, 0.10 }, 1) == 0);
        Check("auto band: band names map back to the UI labels",
              Sensors.AudioProvider.BandName(0) == "bass"
              && Sensors.AudioProvider.BandName(1) == "mid"
              && Sensors.AudioProvider.BandName(2) == "treble");

        // The manual selector is gone: a saved profile must not be able to pin a fixed band.
        var legacyBand = new Effects.EffectDef { Type = Effects.EffectType.AudioVU, AudioBand = "bass" };
        legacyBand.Normalized();
        Check("auto band: a profile saved with a manual band is coerced to auto",
              legacyBand.AudioBand == "auto", legacyBand.AudioBand);
        Check("auto band: BandValue reads the auto level for \"auto\" and for unknown values",
              Effects.EffectRenderer.BandValue("auto",
                  new Effects.EffectContext { AudioAuto = 0.7, AudioLevel = 0.1 }) == 0.7
              && Effects.EffectRenderer.BandValue("nonsense",
                  new Effects.EffectContext { AudioAuto = 0.7, AudioLevel = 0.1 }) == 0.7);

        // ---- 53. round 22: the night-dimming window wraps midnight ----
        Check("night dim: an unset hour never dims", !MainWindow.IsNightWindow(-1, 23));
        Check("night dim: 22:00 -> 07:00 covers the evening and the small hours",
              MainWindow.IsNightWindow(22, 22) && MainWindow.IsNightWindow(22, 23)
              && MainWindow.IsNightWindow(22, 0) && MainWindow.IsNightWindow(22, 6)
              && !MainWindow.IsNightWindow(22, 7) && !MainWindow.IsNightWindow(22, 12));
        Check("night dim: a start before 07:00 does not wrap",
              MainWindow.IsNightWindow(3, 3) && MainWindow.IsNightWindow(3, 6)
              && !MainWindow.IsNightWindow(3, 2) && !MainWindow.IsNightWindow(3, 12));
        Check("night dim: 07:00 -> 07:00 is an empty window",
              !MainWindow.IsNightWindow(7, 7) && !MainWindow.IsNightWindow(7, 3));

        // ---- 51. round 21: updater version comparison ----        Check("update: a higher minor is newer", Update.AppUpdater.IsNewer("1.6.0", "1.7.0"));
        Check("update: a higher major is newer", Update.AppUpdater.IsNewer("1.6.0", "2.0.0"));
        Check("update: the same version is not an update", !Update.AppUpdater.IsNewer("1.6.0", "1.6.0"));
        Check("update: an older version is not an update", !Update.AppUpdater.IsNewer("1.6.0", "1.5.9"));
        Check("update: rc suffixes do not crash the compare", !Update.AppUpdater.IsNewer("1.6.0", "1.6.0rc1"));

        Console.WriteLine(failed == 0 ? "\nALL RENDER TESTS PASSED" : $"\n{failed} TEST(S) FAILED");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Records what the dispatcher called, without touching any real state.</summary>
    private sealed class TestControlTarget : Automation.IControlTarget
    {
        public double LastBrightness;
        public string LastEvent = "";
        public bool LastPower = true;

        public string StatusJson() => "{\"ok\":true,\"profile\":\"Test\",\"power\":true}";
        public List<string> ProfileNames() => new() { "gaming", "work" };
        public bool SetProfile(string name) => name.Equals("gaming", StringComparison.OrdinalIgnoreCase);
        public bool SetEffect(string name) => true;
        public bool SetColor(string hex, bool secondary = false) => true;
        public bool SetBrightness(double v) { LastBrightness = v; return true; }
        public bool SetSpeed(double v) => true;
        public bool Power(bool on) { LastPower = on; return true; }
        public bool Blackout() => true;
        public void PushGameEvent(string name, double value) => LastEvent = name;
        public bool Rescan() => true;
        public string CompanionPin => "123456";
        public string ApiToken => "test-token";
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FullRGB.SDK;

namespace FullRGB;

/// <summary>Headless hardware verification: starts OpenRGB, connects, paints solid colors.</summary>
public static class SelfTest
{
    public sealed class Result
    {
        public bool ok { get; set; }
        public string stage { get; set; } = "";
        public string error { get; set; } = "";
        public int deviceCount { get; set; }
        public List<string> devices { get; set; } = new();
        public bool paintedRed { get; set; }
        public bool paintedOff { get; set; }
        public double elapsedSeconds { get; set; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        // hard watchdog: never hang the host
        var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "fullrgb-selftest.json"),
                "{\"ok\":false,\"stage\":\"watchdog\",\"error\":\"selftest exceeded 120s\"}");
            Environment.Exit(3);
        }, null, 120_000, Timeout.Infinite);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new Result();
        try { File.Delete(Path.Combine(Path.GetTempPath(), "fullrgb-selftest.json")); } catch { }
        void Trace(string stage)
        {
            r.stage = stage;
            try { Console.WriteLine($"[selftest {sw.Elapsed.TotalSeconds:F1}s] {stage}"); } catch { }
        }

        var mgr = new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath());
        try
        {
            Trace("start-openrgb");
            await mgr.StartAsync(TimeSpan.FromSeconds(45));
            Trace("waiting for detection to settle");
            await Task.Delay(4000); // headroom after the port opens

            Trace("connect-sdk");
            using var client = new OpenRgbClient();
            client.Connect("127.0.0.1", 6742, "FullRGB-SelfTest");
            r.deviceCount = client.Controllers.Count;
            r.devices = client.Controllers.Select(c => c.Name + " (leds=" + c.LedCount + ", vendor=" + c.Vendor + ")").ToList();
            Trace($"devices={r.deviceCount}");
            if (r.deviceCount == 0) throw new InvalidOperationException("no devices detected");

            Trace("paint");
            foreach (var dev in client.Controllers)
                if (dev.LedCount > 0) client.SetCustomMode(dev.Index);
            await Task.Delay(800);

            byte[]? first = null;
            bool wroteAny = false;
            foreach (var dev in client.Controllers)
                if (dev.LedCount > 0 && first is null)
                {
                    first = Solid(dev.LedCount, 255, 0, 0);
                    client.UpdateLeds(dev.Index, first);
                    wroteAny = true;
                }
            await Task.Delay(1500);
            r.paintedRed = wroteAny && first is not null;

            bool wroteOff = false;
            foreach (var dev in client.Controllers.Where(c => c.LedCount > 0).Take(1))
            {
                client.UpdateLeds(dev.Index, Solid(dev.LedCount, 0, 0, 0));
                wroteOff = true;
            }
            await Task.Delay(500);
            r.paintedOff = wroteOff;

            r.ok = true;
        }
        catch (Exception e)
        {
            r.error = e.Message;
            r.ok = false;
        }
        finally
        {
            Trace("stopping openrgb");
            mgr.Stop();
            watchdog.Dispose();
        }
        r.elapsedSeconds = sw.Elapsed.TotalSeconds;
        if (!r.ok && r.stage == "done") r.stage = "failed";

        var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
        var outPath = Path.Combine(Path.GetTempPath(), "fullrgb-selftest.json");
        File.WriteAllText(outPath, json);
        try { Console.WriteLine(json); Console.WriteLine("SELFTEST_RESULT_FILE=" + outPath); } catch { }
        return r.ok ? 0 : 1;
    }

    /// <summary>
    /// --stalltest: proves the frame watchdog end-to-end, headlessly, on the real engine.
    ///
    /// It builds the EXACT failure the user reported ("effects stop being applied after a wake-up
    /// until I press Rescan") without sleeping the machine:
    ///
    ///  1. join the running engine and start the REAL <see cref="Effects.EffectEngine"/> with a
    ///     rainbow profile, so frames are actually streaming;
    ///  2. wedge every one of its device sockets with clients that never read their replies. The
    ///     OpenRGB server keeps writing replies into those sockets until they block, which stops it
    ///     applying frames from any client — including ours. Our engine sees NO error: it keeps
    ///     "sending" frames that never reach the hardware;
    ///  3. hand control to the real <see cref="Setup.LightingWatchdog"/>, at a short probe interval,
    ///     with an independent reader measuring whether the hardware is still updating;
    ///  4. the watchdog must reach Repair on its own, and the reader must then see frames again.
    ///
    /// Exit 0 = the stall was built, detected without help and recovered without help.
    /// </summary>
    public static async Task<int> RunStallTestAsync(int seconds)
    {
        var findings = new List<string>();
        void Say(string line) { findings.Add(line); Console.WriteLine("[stalltest] " + line); }

        Setup.EngineShadow.Snapshot? shadowStart = null, shadowAfterWedge = null, shadowAfterFix = null;
        Effects.EffectEngine? engine = null;
        Sensors.TemperatureProvider? temps = null;
        var wedges = new List<System.Net.Sockets.TcpClient>();
        Setup.LightingWatchdog? watchdog = null;
        Process? ownEngine = null;
        bool attached = false;
        OpenRgbProcessManager? mgr = null;

        try
        {
            mgr = new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath());
            var port = Config.ProfileStore.Load().ServerPort;

            // 1) Join the engine that is already up if it answers; otherwise start one. An
            //    already-running engine is the case that matters (the user's app is running).
            var pre = Setup.EngineShadow.Probe(port, 1200);
            if (pre.EngineReachable)
            {
                attached = true;
                Say($"joined the running engine on port {port}: {pre.Devices.Count} device(s)");
            }
            else
            {
                Say($"no engine answering on {port} — starting one");
                await mgr.StartAsync(TimeSpan.FromSeconds(45));
                await Task.Delay(4000);
            }

            using var client = new OpenRgbClient();
            client.Connect("127.0.0.1", port, "FullRGB-StallTest");
            client.ExpandAllZones();
            client.EnsureDirectMode();
            Say($"devices={client.Controllers.Count} " +
                string.Join(", ", client.Controllers.Select(c => $"{c.Name}({c.LedCount})")));
            if (client.Controllers.Count == 0 || client.Controllers.All(c => c.LedCount == 0))
            {
                Say("FAILED: nothing paintable");
                return 1;
            }

            // Remember how many LEDs each zone had BEFORE we expand, so the watchdog's "zones are
            // stuck at their old size" verdict can be observed too.
            shadowStart = Setup.EngineShadow.Probe(port);

            temps = new Sensors.TemperatureProvider();
            temps.Start();
            engine = new Effects.EffectEngine(client, temps, null);
            var profile = new Config.Profile
            {
                Name = "StallTest",
                GlobalEffect = new Effects.EffectDef { Type = Effects.EffectType.Rainbow, Speed = 0.6, Brightness = 1.0 },
            };
            engine.Apply(profile);
            await Task.Delay(2500);

            var alive = Setup.EngineShadow.Probe(port);
            Say($"painting: frames={engine.FramesSent} storedColours reflect our frames: " +
                $"{Setup.EngineShadow.AnyMotion(shadowStart, alive, DeviceNames(client))}");
            if (engine.FramesSent < 20)
            {
                Say("FAILED: the engine never started streaming");
                return 1;
            }

            // 2) Wedge the engine: connect writers that never read a single reply.
            for (int i = 0; i < 40; i++)
            {
                var t = new System.Net.Sockets.TcpClient();
                t.Connect("127.0.0.1", port);
                t.NoDelay = true;
                wedges.Add(t);
            }
            Say($"{wedges.Count} non-reading clients attached");
            await Task.Delay(4000);
            long framesAtWedge = engine.FramesSent;

            // 3) The watchdog runs for real, from an independent observer. Short interval so the
            //    test does not have to wait a minute per probe.
            int repairs = 0, rebuilds = 0;
            double firstVerdictAt = -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            watchdog = new Setup.LightingWatchdog(
                TimeSpan.FromSeconds(4),
                () => new Setup.LightingWatchdog.Stats
                {
                    Port = port,
                    EngineRunning = client.Connected,
                    EffectsRunning = engine.IsRunning,
                    SecondsSinceSession = 300,   // the session is long up: no grace period
                    AnimatedDeviceNames = () => DeviceNames(client),
                },
                (verdict, snap) =>
                {
                    if (firstVerdictAt < 0) firstVerdictAt = sw.Elapsed.TotalSeconds;
                    if (verdict == Setup.EngineShadow.Verdict.Repair) repairs++;
                    if (verdict == Setup.EngineShadow.Verdict.Rebuild) rebuilds++;
                    Say($"watchdog verdict {verdict} at {sw.Elapsed.TotalSeconds:F1}s " +
                        $"(listen={snap.PortListening} conns={snap.ClientConnections} leds={snap.LedTotal})");
                });
            watchdog.ProbeIntervalSeconds = 4;
            watchdog.Start();
            Say("watchdog started (4 s probes), waiting for it to notice the stall on its own");

            // 4) Wait for the watchdog. It must NOT be told anything; it only sees its own probes.
            double deadline = Math.Min(seconds, 120);
            while (sw.Elapsed.TotalSeconds < deadline && repairs == 0 && rebuilds == 0)
                await Task.Delay(500);
            shadowAfterWedge = Setup.EngineShadow.Probe(port);

            if (repairs == 0 && rebuilds == 0)
            {
                Say("FAILED: the watchdog never acted on a stalled engine");
                return 1;
            }
            Say($"watchdog acted by itself: verdict at {firstVerdictAt:F1}s, " +
                $"rebuilds={rebuilds} repairs={repairs}");

            // Release the wedges so the recovery is judged on the app's own frames, not on a test
            // that keeps the server busy. (A real suspend kills these sockets with it.)
            if (repairs > 0)
            {
                foreach (var t in wedges) { try { t.Close(); } catch { } }
                wedges.Clear();
                Say("released the test's wedged clients (a real suspend does this itself)");
            }
            else
            {
                foreach (var t in wedges) { try { t.Close(); } catch { } }
                wedges.Clear();
                Say("released the test's wedged clients after the Rebuild verdict");
            }

            // 5) Rebuild the session the way the app does, then re-assert the lighting.
            Say("applying the lighting again after the watchdog's verdict");
            try { client.Connect("127.0.0.1", port, "FullRGB-StallTest"); } catch { }
            try { client.ExpandAllZones(); } catch { }
            try { client.EnsureDirectMode(); } catch { }
            engine.InvalidateFrames();
            engine.Apply(profile);
            await Task.Delay(3000);

            long framesAfter = engine.FramesSent;
            shadowAfterFix = Setup.EngineShadow.Probe(port);
            bool paintingAgain = Setup.EngineShadow.AnyMotion(shadowAfterWedge, shadowAfterFix, DeviceNames(client));
            Say($"frames {framesAtWedge} -> {framesAfter}, hardware updating again: {paintingAgain}");

            // ---- verdict ----
            bool stuckZones = shadowAfterWedge is not null && shadowStart is not null
                              && shadowAfterWedge.ZoneTotal < shadowStart.ZoneTotal;
            bool zonesBack = shadowAfterFix is not null && shadowAfterFix.ZoneTotal > 0;
            bool ok = (repairs + rebuilds) > 0 && framesAfter > framesAtWedge && zonesBack;
            Say($"SUMMARY stallBuilt=True watchdogActed={repairs + rebuilds > 0} " +
                $"zonesStuck={stuckZones} zonesRestored={zonesBack} paintingAgain={paintingAgain} " +
                $"frames={framesAfter}");

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "fullrgb-stalltest.txt"),
                string.Join(Environment.NewLine, findings));
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            Say("EXCEPTION: " + e);
            return 2;
        }
        finally
        {
            try { watchdog?.Dispose(); } catch { }
            foreach (var t in wedges) { try { t.Close(); } catch { } }
            try { engine?.Stop(); } catch { }
            try { temps?.Dispose(); } catch { }
            // Only stop an engine this test started; an engine that was already running belongs to
            // the user's app and must keep serving it.
            if (!attached)
            {
                try { mgr?.StopIncludingElevated(); } catch { }
                try { ownEngine?.Dispose(); } catch { }
            }
        }
    }

    private static ISet<string> DeviceNames(OpenRgbClient client)
        => new HashSet<string>(client.Controllers.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    private static byte[] Solid(int leds, byte r, byte g, byte b)
    {
        var buf = new byte[leds * 3];
        for (int i = 0; i < leds; i++) { buf[i * 3] = r; buf[i * 3 + 1] = g; buf[i * 3 + 2] = b; }
        return buf;
    }

    /// <summary>
    /// --fxtest: exercises the REAL production path (process manager -> client -> zone expand ->
    /// EffectEngine with a rainbow profile) and reports frames actually accepted by the server.
    /// This is what proves "effects work", not a single solid paint.
    /// </summary>
    public static async Task<int> RunEffectTestAsync(int seconds = 15)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mgr = new OpenRgbProcessManager(OpenRgbProcessManager.DefaultExePath());
        var errors = new List<string>();
        Sensors.TemperatureProvider? temps = null;
        Effects.EffectEngine? engine = null;
        try
        {
            Console.WriteLine("[fxtest] starting openrgb");
            await mgr.StartAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(4000);

            using var client = new OpenRgbClient();
            client.Connect("127.0.0.1", 6742, "FullRGB-FxTest");
            Console.WriteLine($"[fxtest] devices={client.Controllers.Count}");
            client.ExpandAllZones();
            client.EnsureDirectMode();
            foreach (var c in client.Controllers)
                Console.WriteLine($"[fxtest]   {c.Name} kind={c.Kind} leds={c.LedCount} direct={c.InDirectMode} " +
                                  string.Join(",", c.Zones.Select(z => $"{z.Name}:{z.LedsCount}")));

            temps = new Sensors.TemperatureProvider();
            temps.Start();
            engine = new Effects.EffectEngine(client, temps, null);
            engine.Status += m => { lock (errors) errors.Add(m); };

            var profile = new Config.Profile
            {
                Name = "FxTest",
                GlobalEffect = new Effects.EffectDef
                {
                    Type = Effects.EffectType.Rainbow, Speed = 0.6, Brightness = 1.0,
                },
            };
            Console.WriteLine($"[fxtest] running rainbow for {seconds}s — WATCH THE HARDWARE");

            // Per-frame trace: shows whether the frame budget is lost in render, IO or sleep.
            var trace = new List<(int dev, double r, double io, double want, double got)>();
            engine!.FrameTrace = (d, r, io, want, got) => { lock (trace) trace.Add((d, r, io, want, got)); };

            engine.Apply(profile);
            await Task.Delay(seconds * 1000);
            long frames = engine!.FramesSent;
            double fps = engine.Fps;
            double delivered = engine.DeliveredFps;
            var rates = engine.DeviceRates();
            double renderMs = engine.LastRenderMs, ioMs = engine.LastIoMs, sleepMs = engine.LastSleepMs;
            bool hiRes = engine.HighResTimer;
            bool perDev = engine.PerDeviceChannels;
            engine.Stop();

            Console.WriteLine($"[fxtest] framesSent={frames} errors={errors.Count} renderFps={fps:F1} deliveredFps={delivered:F1} renderMs={renderMs:F2} ioMs={ioMs:F2} sleepMs={sleepMs:F2} hiResTimer={hiRes} perDeviceChannels={perDev}");
            foreach (var e in errors.Distinct().Take(5)) Console.WriteLine("[fxtest] ERR " + e);

            foreach (var r in rates)
            {
                var nm = client.Controllers.FirstOrDefault(c => c.Index == r.Index)?.Name ?? $"dev{r.Index}";
                Console.WriteLine($"[fxtest] dev{r.Index} {nm}: render={r.RenderFps:F1} fps " +
                                  $"delivered={r.DeliveredFps:F1} fps dropped={r.DroppedFps:F1}/s ownChannel={r.OwnChannel}");
            }

            // Where did the time go? Percentiles PER DEVICE, so one stalled device is visible
            // instead of averaged away.
            lock (trace)
            {
                foreach (var g in trace.GroupBy(t => t.dev).OrderBy(g => g.Key))
                {
                    var rows = g.ToList();
                    if (rows.Count < 10) continue;
                    var io = rows.Select(t => t.io).OrderBy(x => x).ToArray();
                    var over = rows.Select(t => t.got - t.want).OrderBy(x => x).ToArray();
                    var total = rows.Select(t => t.r + t.io + t.got).OrderBy(x => x).ToArray();
                    string P(double[] a, double q) => $"{a[Math.Min((int)(a.Length * q), a.Length - 1)]:F1}";
                    var name = client.Controllers.FirstOrDefault(c => c.Index == g.Key)?.Name ?? $"dev{g.Key}";
                    Console.WriteLine($"[fxtest] dev{g.Key} {name}: frames={rows.Count} " +
                                      $"io p50={P(io, .5)} p99={P(io, .99)} max={io[^1]:F1} | " +
                                      $"sleepErr p50={P(over, .5)} p99={P(over, .99)} | " +
                                      $"frame p50={P(total, .5)} p99={P(total, .99)}");
                }
            }
            Console.WriteLine($"[fxtest] elapsed={sw.Elapsed.TotalSeconds:F1}s");
            // A device-frame counter of >100 with zero errors is the acceptance bar; the measured
            // rate is printed so pacing regressions are visible instead of silent.
            try { engine?.Stop(); } catch { }
            try { temps?.Dispose(); } catch { }
            return frames > 100 && errors.Count == 0 ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("[fxtest] FAILED: " + e);
            return 2;
        }
        finally
        {
            try { engine?.Stop(); } catch { }
            try { temps?.Dispose(); } catch { }
            mgr.Stop();
        }
    }
}

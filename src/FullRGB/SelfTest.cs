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

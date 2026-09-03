using System.Diagnostics;
using FullRGB.Config;
using FullRGB.SDK;
using FullRGB.Sensors;

namespace FullRGB.Effects;

/// <summary>
/// Runs the effect loops (~30 fps) and pushes LED frames to OpenRGB per ZONE
/// (fans/pump only respond to zone writes). Resolves zone > device > global effect overrides
/// and recovers automatically if the OpenRGB server dies.
///
/// THREADING (round 8, every claim measured — probes in <c>_probe/</c>):
/// each device gets its own render loop, its own write-only SDK socket and its own writer thread
/// (<see cref="DeviceChannel"/>). Rationale in that class; the short version is that the OpenRGB
/// server writes to USB synchronously per client connection, devices have wildly different real
/// capacities (ASUS board ~50 frame/s vs Corsair Commander Core ~19.7 frame/s for per-zone
/// writes), and a single overrun <c>send()</c> can block for seconds. Rendering into a one-slot
/// mailbox keeps every render loop at a steady 30 fps and simply drops stale frames for slow
/// devices. The shared <see cref="OpenRgbClient"/> keeps sole ownership of request/response
/// traffic (discovery, resize, mode changes), so protocol exchanges never interleave.
/// </summary>
public sealed class EffectEngine : IDisposable
{
    private const int TargetFps = 30;

    private readonly OpenRgbClient _client;
    private readonly TemperatureProvider _temps;
    private readonly AudioProvider? _audio;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _deviceLoops = new();
    private Task? _sensorLoop;
    private Profile? _profile;
    private readonly object _profileLock = new();
    private readonly object _lifecycleLock = new();
    private readonly Dictionary<string, ulong> _lastFrame = new();
    private long _framesSent;

    /// <summary>Device keys the running loops were built for; a rescan must restart them.</summary>
    private string[] _loopDevices = Array.Empty<string>();

    /// <summary>Bumped whenever the SDK is revived, telling every loop to reopen its channel.</summary>
    private int _generation;
    private readonly object _reviveLock = new();

    // Per-device statistics, keyed by device index.
    private readonly object _statLock = new();
    private readonly Dictionary<int, DeviceStats> _stats = new();

    private sealed class DeviceStats
    {
        public double Fps, RenderMs, IoMs, SleepMs;
        public bool HighResTimer;
        public bool UsingOwnChannel;
        /// <summary>Frames the writer thread actually put on the wire, per second.</summary>
        public double DeliveredFps;
        /// <summary>Frames superseded before the writer could send them, per second.</summary>
        public double DroppedFps;
    }

    // Sensors are read on their own thread: LibreHardwareMonitor's Update() can block for
    // tens of milliseconds, which used to stall the render loop (and, in the UI, the dispatcher).
    private volatile object? _cpuTempBox, _gpuTempBox;
    private double? _cpuTemp { get => (double?)_cpuTempBox; set => _cpuTempBox = value; }
    private double? _gpuTemp { get => (double?)_gpuTempBox; set => _gpuTempBox = value; }

    /// <summary>
    /// Called when the SDK connection cannot be restored by a plain reconnect: should restart the
    /// OpenRGB process, reconnect the client, re-expand zones and return true on success.
    /// </summary>
    public Func<CancellationToken, bool>? ReviveEngine { get; set; }

    /// <summary>
    /// Optional per-frame trace sink: (deviceIndex, renderMs, ioMs, sleepRequestedMs, sleepActualMs).
    /// Used by --fxtest to show where the frame budget goes.
    /// </summary>
    public Action<int, double, double, double, double>? FrameTrace { get; set; }

    public bool IsRunning => _deviceLoops.Count > 0 && _deviceLoops.Any(t => !t.IsCompleted);

    /// <summary>LED frames accepted by the server since start (verification hook).</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>
    /// Worst device's RENDER rate — the honest number for a status line, because an average
    /// across a stalled device and a fast one would hide the stall.
    /// </summary>
    public double Fps
    {
        get
        {
            lock (_statLock)
                return _stats.Count == 0 ? 0 : _stats.Values.Min(s => s.Fps);
        }
    }

    /// <summary>
    /// Worst device's DELIVERED rate (frames actually written to hardware). Lower than
    /// <see cref="Fps"/> on devices that cannot keep up; that is by design, not an error.
    /// </summary>
    public double DeliveredFps
    {
        get
        {
            lock (_statLock)
                return _stats.Count == 0 ? 0 : _stats.Values.Min(s => s.DeliveredFps);
        }
    }

    /// <summary>
    /// Per-device snapshot for diagnostics and the UI: render rate, rate actually delivered to
    /// hardware, stale frames dropped, and whether the device has its own socket.
    /// </summary>
    public List<(int Index, double RenderFps, double DeliveredFps, double DroppedFps, bool OwnChannel)> DeviceRates()
    {
        lock (_statLock)
            return _stats.OrderBy(kv => kv.Key)
                         .Select(kv => (kv.Key, kv.Value.Fps, kv.Value.DeliveredFps,
                                        kv.Value.DroppedFps, kv.Value.UsingOwnChannel))
                         .ToList();
    }

    /// <summary>Total ms per frame spent COMPUTING pixels across all devices.</summary>
    public double LastRenderMs => Stat(s => s.RenderMs, sum: true);
    /// <summary>Worst device's ms per frame spent WRITING to the SDK.</summary>
    public double LastIoMs => Stat(s => s.IoMs, sum: false);
    /// <summary>Worst device's ms per frame spent sleeping.</summary>
    public double LastSleepMs => Stat(s => s.SleepMs, sum: false);
    /// <summary>True when every loop got an accurate OS timer (see <see cref="PreciseTimer"/>).</summary>
    public bool HighResTimer
    {
        get { lock (_statLock) return _stats.Count > 0 && _stats.Values.All(s => s.HighResTimer); }
    }
    /// <summary>True when every device is streaming over its own socket (the fast path).</summary>
    public bool PerDeviceChannels
    {
        get { lock (_statLock) return _stats.Count > 0 && _stats.Values.All(s => s.UsingOwnChannel); }
    }

    private double Stat(Func<DeviceStats, double> pick, bool sum)
    {
        lock (_statLock)
        {
            if (_stats.Count == 0) return 0;
            return sum ? _stats.Values.Sum(pick) : _stats.Values.Max(pick);
        }
    }

    /// <summary>Latest sensor values, safe to read from the UI thread (no hardware access).</summary>
    public double? CpuTemp => _cpuTemp;
    public double? GpuTemp => _gpuTemp;
    public event Action<string>? Status;

    public EffectEngine(OpenRgbClient client, TemperatureProvider temps, AudioProvider? audio)
    {
        _client = client;
        _temps = temps;
        _audio = audio;
    }

    public void Apply(Profile profile)
    {
        List<Task> toStopOutside = new();
        lock (_lifecycleLock)
        {
            lock (_profileLock) _profile = Clone(profile);
            lock (_lastFrame) _lastFrame.Clear();

            // A rescan can add or remove devices; the loops are per device, so rebuild them.
            var current = PaintableDevices().Select(d => d.Key).ToArray();
            if (IsRunning && !current.SequenceEqual(_loopDevices)) StopLocked();
            if (!IsRunning) StartLocked();
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock) StopLocked();
    }

    private void StopLocked()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        // Copy first: DeviceLoop never touches _deviceLoops, but Apply may run concurrently.
        var loops = _deviceLoops.ToArray();
        _deviceLoops.Clear();
        var sensor = _sensorLoop;
        _sensorLoop = null;
        // Release the lock while waiting so a concurrent Apply doesn't deadlock;
        // loops only read _profile/_client which stay valid.
        Monitor.Exit(_lifecycleLock);
        try
        {
            foreach (var t in loops)
            {
                try { t.Wait(2000); } catch { /* cancellation */ }
            }
            try { sensor?.Wait(1500); } catch { /* cancellation */ }
        }
        finally
        {
            Monitor.Enter(_lifecycleLock);
            try { cts?.Dispose(); } catch { }
            lock (_statLock) _stats.Clear();
            _loopDevices = Array.Empty<string>();
        }
    }

    /// <summary>Paints everything black once (used when the user stops effects).</summary>
    public void Blackout()
    {
        foreach (var dev in _client.Controllers)
        {
            if (dev.LedCount == 0) continue;
            try
            {
                foreach (var zone in dev.Zones.Where(z => z.LedsCount > 0))
                    _client.UpdateZoneLeds(dev.Index, zone.Index, new byte[zone.LedsCount * 3]);
            }
            catch { }
        }
        lock (_lastFrame) _lastFrame.Clear();
    }

    private List<RgbController> PaintableDevices()
        => _client.Controllers.Where(d => d.LedCount > 0 && d.Zones.Any(z => z.LedsCount > 0)).ToList();

    private void Start()
    {
        lock (_lifecycleLock) StartLocked();
    }

    private void StartLocked()
    {
        // Devices must be in Direct mode before per-LED writes do anything visible.
        // This runs once at engine start, not on every Apply (slider drags otherwise flood the SDK).
        try { _client.EnsureDirectMode(); } catch { }

        var devices = PaintableDevices();
        _loopDevices = devices.Select(d => d.Key).ToArray();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _sensorLoop = Task.Run(() => SensorLoop(token), token);

        foreach (var dev in devices)
        {
            int index = dev.Index;
            lock (_statLock) _stats[index] = new DeviceStats();
            // LongRunning = a dedicated thread: the loop blocks on a waitable timer and must not
            // occupy (or wait for) a thread-pool slot.
            _deviceLoops.Add(Task.Factory.StartNew(() => DeviceLoop(index, token), token,
                                                   TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }
    }

    /// <summary>One independent 30 fps render loop per device; writes happen on the channel's thread.</summary>
    private void DeviceLoop(int deviceIndex, CancellationToken token)
    {
        using var timer = new PreciseTimer();
        // Capture the handle ONCE: Stop() disposes the CTS after cancel, and touching
        // token.WaitHandle afterwards throws ObjectDisposedException.
        WaitHandle cancelHandle;
        try { cancelHandle = token.WaitHandle; } catch { return; }
        var audioState = new AudioState(); // peak-hold meter memory, one per device loop
        DeviceChannel? channel = null;
        int myGeneration = Volatile.Read(ref _generation);
        int failures = 0;

        var stats = new DeviceStats { HighResTimer = timer.IsHighResolution };
        lock (_statLock) _stats[deviceIndex] = stats;

        var sw = Stopwatch.StartNew();
        double targetFrameMs = 1000.0 / TargetFps;
        double next = sw.Elapsed.TotalMilliseconds;
        double windowStart = next;
        int windowFrames = 0;
        double accRender = 0, accIo = 0, accSleep = 0;
        long windowDelivered = 0, windowDropped = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                double renderMs = 0, ioMs = 0;
                try
                {
                    // The SDK was revived under us, or our writer thread died: reopen.
                    int gen = Volatile.Read(ref _generation);
                    if (gen != myGeneration || channel is { Fault: not null })
                    {
                        var fault = channel?.Fault;
                        channel?.Dispose();
                        channel = null;
                        myGeneration = gen;
                        if (fault is not null) Status?.Invoke($"dev{deviceIndex} write failed: {fault.Message}");
                    }

                    if (channel is null)
                    {
                        channel = TryOpenChannel(deviceIndex);
                        bool own = channel is not null;
                        lock (_statLock) stats.UsingOwnChannel = own;
                        if (channel is not null)
                        {
                            windowDelivered = 0;
                            windowDropped = 0;
                            // A fresh socket means the device may hold stale colours, and the
                            // dedupe cache would suppress a static effect forever. Forget this
                            // device's hashes so the next frame is always written.
                            string prefix = deviceIndex + ":";
                            lock (_lastFrame)
                            {
                                foreach (var k in _lastFrame.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                                    _lastFrame.Remove(k);
                            }
                        }
                    }

                    var dev = _client.Controllers.FirstOrDefault(c => c.Index == deviceIndex);
                    if (dev is null)
                    {
                        // device vanished (rescan in flight) — idle until Apply() rebuilds the loops
                        if (!timer.Wait(250, cancelHandle)) break;
                        continue;
                    }

                    Profile profile;
                    lock (_profileLock) profile = _profile ?? new Profile();

                    var ctx = new EffectContext
                    {
                        Time = sw.Elapsed.TotalSeconds,
                        CpuTemp = _cpuTemp,
                        GpuTemp = _gpuTemp,
                        AudioLevel = _audio?.Level ?? 0,
                        AudioBass = _audio?.Bass ?? 0,
                        AudioMid = _audio?.Mid ?? 0,
                        AudioTreble = _audio?.Treble ?? 0,
                        Beat = _audio?.Beat ?? 0,
                    };

                    (renderMs, ioMs) = RenderDevice(dev, profile, ctx, channel, audioState);
                    failures = 0;

                    windowFrames++;
                    accRender += renderMs;
                    accIo += ioMs;
                    double sinceMs = sw.Elapsed.TotalMilliseconds - windowStart;
                    if (sinceMs >= 1000)
                    {
                        lock (_statLock)
                        {
                            stats.Fps = windowFrames * 1000.0 / sinceMs;
                            stats.RenderMs = accRender / windowFrames;
                            stats.IoMs = accIo / windowFrames;
                            stats.SleepMs = accSleep / windowFrames;
                            if (channel is not null)
                            {
                                long d = channel.Delivered, dr = channel.Dropped;
                                stats.DeliveredFps = (d - windowDelivered) * 1000.0 / sinceMs;
                                stats.DroppedFps = (dr - windowDropped) * 1000.0 / sinceMs;
                                windowDelivered = d;
                                windowDropped = dr;
                            }
                            else
                            {
                                // shared-client fallback: every rendered frame was written inline
                                stats.DeliveredFps = stats.Fps;
                                stats.DroppedFps = 0;
                            }
                        }
                        windowFrames = 0;
                        accRender = accIo = accSleep = 0;
                        windowStart = sw.Elapsed.TotalMilliseconds;
                    }
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested) break;
                    failures++;
                    Status?.Invoke($"effect-loop dev{deviceIndex}: {e.Message}");

                    // Our own socket is the most likely casualty; drop it and let the next
                    // iteration reopen. Only escalate to a full SDK revive when that keeps failing.
                    channel?.Dispose();
                    channel = null;
                    lock (_statLock) stats.UsingOwnChannel = false;

                    if (ShouldAttemptRecovery(failures, token.IsCancellationRequested))
                        Revive(token);

                    if (!timer.Wait(failures > 5 ? 2000 : 300, cancelHandle)) break;
                    next = sw.Elapsed.TotalMilliseconds;
                }

                // Fixed-rate pacing on a high-resolution timer: Task.Delay/Thread.Sleep round up
                // to the ~15.6 ms scheduler tick, which cost ~10 fps (see PreciseTimer).
                //
                // The loop deliberately renders at the FULL target rate even for a device that
                // can only apply ~3 frames/s: rendering costs 0.04 ms (≈0.1% of one core), and
                // always having a freshly rendered frame in the mailbox means the device gets the
                // NEWEST possible frame the moment it becomes free — slowing the loop to the
                // device's rate would only add latency to profile changes.
                next += targetFrameMs;
                double wait = next - sw.Elapsed.TotalMilliseconds;
                if (wait < 0)
                {
                    next = sw.Elapsed.TotalMilliseconds;   // fell behind: resync, don't burst
                    wait = 0;
                }
                double sleepStart = sw.Elapsed.TotalMilliseconds;
                if (!timer.Wait(wait, cancelHandle)) break;
                double slept = sw.Elapsed.TotalMilliseconds - sleepStart;
                accSleep += slept;
                FrameTrace?.Invoke(deviceIndex, renderMs, ioMs, wait, slept);
            }
        }
        finally
        {
            channel?.Dispose();
        }
    }

    /// <summary>
    /// Opens a private write-only socket for one device. Returns null when the server refuses,
    /// in which case the loop falls back to the shared client (slower, but still lights up).
    /// </summary>
    private DeviceChannel? TryOpenChannel(int deviceIndex)
    {
        try
        {
            var ch = new DeviceChannel(deviceIndex, _client.Host, _client.Port,
                                       _client.ClientName, _client.ProtocolVersion);
            ch.Open();
            return ch;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Restores the SDK after a failure. Serialized: several device loops fail at once when the
    /// server dies, and they must not each restart OpenRGB.
    /// </summary>
    private void Revive(CancellationToken token)
    {
        lock (_reviveLock)
        {
            if (_client.IsAlive) return;   // another loop already fixed it

            bool back = false;
            try { _client.Reconnect(token); back = true; } catch { }
            if (!back && ReviveEngine is not null)
            {
                try { back = ReviveEngine(token); } catch { }
            }
            if (back)
            {
                Status?.Invoke("reconnected to RGB engine");
                lock (_lastFrame) _lastFrame.Clear();
                Interlocked.Increment(ref _generation);   // every loop reopens its channel
            }
        }
    }

    /// <summary>
    /// Renders every zone of one device and hands the frame off. Returns (renderMs, ioMs);
    /// with a channel, ioMs is only the cost of the non-blocking hand-off.
    /// </summary>
    private (double renderMs, double ioMs) RenderDevice(RgbController dev, Profile profile,
                                                        EffectContext ctx, DeviceChannel? channel,
                                                        AudioState? audioState)
    {
        if (profile.IsExcluded(dev)) return (0, 0);

        double renderMs = 0, ioMs = 0;
        var clock = Stopwatch.StartNew();
        List<ZonePaint>? frame = channel is null ? null : new List<ZonePaint>(dev.Zones.Count);
        bool anySent = false;

        // Zone-wise rendering: each zone is a physically separate strip/fan/ring,
        // so an effect spans one zone rather than the flattened device buffer.
        foreach (var zone in dev.Zones)
        {
            if (zone.LedsCount == 0) continue;

            // Per-zone override beats per-device beats global.
            var eff = profile.EffectFor(dev, zone);
            int n = (int)zone.LedsCount;

            // Phase seed decides whether zones look identical or offset from each other.
            // Sync = every zone gets seed 0, so "solid cyan" really is the same cyan
            // on the pump, the fans and the board.
            int seed = eff.SyncZones ? 0 : dev.Index * 7 + zone.Index + 1;

            double t0 = clock.Elapsed.TotalMilliseconds;
            var rgb = EffectRenderer.Render(eff, n, seed, ctx, audioState);
            profile.CalibrationFor(dev, zone).Apply(rgb);
            renderMs += clock.Elapsed.TotalMilliseconds - t0;

            string key = $"{dev.Index}:{zone.Index}";
            ulong hash = Hash(rgb);
            bool skip;
            lock (_lastFrame)
            {
                skip = _lastFrame.TryGetValue(key, out var prev) && prev == hash;
                if (!skip) _lastFrame[key] = hash;
            }
            if (skip) continue;

            if (frame is not null)
            {
                frame.Add(new ZonePaint(zone.Index, rgb));
            }
            else
            {
                // Fallback path (no private channel): write inline on the shared client.
                double t1 = clock.Elapsed.TotalMilliseconds;
                _client.UpdateZoneLeds(dev.Index, zone.Index, rgb);
                ioMs += clock.Elapsed.TotalMilliseconds - t1;
            }
            anySent = true;
        }

        if (frame is { Count: > 0 })
        {
            double t2 = clock.Elapsed.TotalMilliseconds;
            channel!.Submit(frame.ToArray());   // never blocks: latest frame wins
            ioMs += clock.Elapsed.TotalMilliseconds - t2;
        }

        if (anySent) Interlocked.Increment(ref _framesSent);
        return (renderMs, ioMs);
    }

    /// <summary>Reads temperatures twice a second, off the render threads.</summary>
    private void SensorLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var (cpu, gpu) = _temps.Read();
                _cpuTemp = cpu;
                _gpuTemp = gpu;
            }
            catch { /* sensors are optional */ }
            try { Task.Delay(500, token).Wait(token); }
            catch { break; }
        }
    }

    /// <summary>Recovery is only attempted on real failures, never while stopping.</summary>
    internal static bool ShouldAttemptRecovery(int consecutiveFailures, bool cancellationRequested)
        => !cancellationRequested && (consecutiveFailures is 1 or 5 or 20);

    private static ulong Hash(byte[] rgb)
    {
        ulong h = 1469598103934665603UL;
        foreach (var b in rgb) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    public static EffectDef Clone(EffectDef e) => new()
    {
        Type = e.Type, ColorHex = e.ColorHex, Color2Hex = e.Color2Hex, Color3Hex = e.Color3Hex,
        Speed = e.Speed, Brightness = e.Brightness, TempSensor = e.TempSensor,
        TempLow = e.TempLow, TempHigh = e.TempHigh, CustomPixels = e.CustomPixels,
        Direction = e.Direction, SyncZones = e.SyncZones, AudioBand = e.AudioBand, AudioGain = e.AudioGain,
        BeatStrength = e.BeatStrength,
        AudioMode = e.AudioMode, AudioColor = e.AudioColor, AudioBgHex = e.AudioBgHex,
        PeakHold = e.PeakHold, UsePalette = e.UsePalette,
        ExtraColors = new List<string>(e.ExtraColors ?? new List<string>()),
    };

    public static Profile Clone(Profile p) => new()
    {
        Name = p.Name,
        GlobalEffect = Clone(p.GlobalEffect),
        DeviceOverrides = p.DeviceOverrides.ToDictionary(kv => kv.Key, kv => Clone(kv.Value)),
        ZoneOverrides = p.ZoneOverrides.ToDictionary(kv => kv.Key, kv => Clone(kv.Value)),
        ExcludedDevices = new List<string>(p.ExcludedDevices),
        ZoneSizes = new Dictionary<string, int>(p.ZoneSizes),
        Calibrations = p.Calibrations.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        ZoneCalibrations = p.ZoneCalibrations.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
    };

    public void Dispose() => Stop();
}

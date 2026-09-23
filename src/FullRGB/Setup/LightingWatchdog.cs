using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace FullRGB.Setup;

/// <summary>
/// The watchdog that answers "is the machine still dark?" WITHOUT asking the app's own client.
///
/// THE HOLE THIS CLOSES. Every previous recovery path asked the engine a question through the
/// same client the app paints with. After a sleep/hibernate an OpenRGB engine keeps its server
/// thread healthy and answers every request ("sdkAlive", controller data, protocol version) while
/// its USB/HID handles died with the suspend — so "the engine is alive" was reported truthfully
/// and nothing was repaired, while the LEDs stayed dark until the user pressed Rescan. Round 16
/// fixed the wake notification and forced an engine restart on resume; rounds 17-19 added a
/// follow-up rescan. What none of them did was notice the state from an INDEPENDENT observer.
///
/// THE SIGNAL. <see cref="EngineShadow"/> samples, once a minute: whether the SDK port is still
/// listening, how many clients are attached, how many LEDs the engine thinks every zone has, and
/// a hash of the colours it is holding. An engine that is APPLYING does not hold still: every
/// frame the app paints lands in those stored colours. Static colours (Solid, Gradient) are
/// excluded, so a deliberately static effect never looks like a stall.
///
/// THE VERDICT (see <see cref="EngineShadow.Decide"/>, pure and unit-tested):
///   * port gone                       -&gt; Repair (replace the engine)
///   * port alive, nobody attached      -&gt; Repair (the session lost its engine)
///   * zones back to 0 LEDs though we   -&gt; Rebuild (re-expand, no process restart)
///     expanded them
///   * animated effect, two strikes of  -&gt; Repair
///     "not a single colour moved"        (one strike only warns, so a fluke never restarts)
///
/// A Repair is a non-destructive teardown + engine restart + reconnect + re-apply: exactly what
/// closing and reopening the app did, but automatic.
/// </summary>
public sealed class LightingWatchdog : IDisposable
{
    /// <summary>What the watchdog needs to know about the live session.</summary>
    public sealed class Stats
    {
        public int Port = 6742;
        /// <summary>True when a client exists and is connected (NOT necessarily healthy).</summary>
        public bool EngineRunning;
        /// <summary>True when the effect loops are actually streaming frames.</summary>
        public bool EffectsRunning;
        /// <summary>Seconds since the last connect or repair; the grace period uses this.</summary>
        public double SecondsSinceSession = double.MaxValue;
        /// <summary>Devices whose effect changes every frame (excluded from the motion test).</summary>
        public Func<ISet<string>> AnimatedDeviceNames = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How a probe is actually run. Injectable so the SAME class can be tested headlessly.</summary>
    public delegate void ProbeDelegate(int port, Action<EngineShadow.Snapshot> onDone);

    private readonly Func<Stats> _stats;
    private readonly Action<EngineShadow.Verdict, EngineShadow.Snapshot> _act;
    private readonly System.Threading.Timer _timer;
    private readonly object _journalLock = new();
    private readonly List<string> _journal = new();
    private readonly Queue<EngineShadow.Snapshot> _history = new();
    private int _probesSinceGrowth;
    private int _strikes;
    private int _busy;
    private int _lastLedTotal = -1;

    /// <summary>The most recent snapshot, or null when nothing has been probed yet.</summary>
    public EngineShadow.Snapshot? Last { get; private set; }

    /// <summary>Raised whenever a probe finds a device whose stored colours actually moved.</summary>
    public event Action? MotionDetected;

    public ProbeDelegate Prober { get; set; } = (port, done) => done(EngineShadow.Probe(port));

    public LightingWatchdog(TimeSpan interval, Func<Stats> stats,
                            Action<EngineShadow.Verdict, EngineShadow.Snapshot> act)
    {
        _stats = stats;
        _act = act;
        // A plain threading timer, NOT a DispatcherTimer: the loop must tick on a worker thread —
        // it has to keep watching while the UI thread is blocked, and a headless host (--stalltest)
        // has no message pump to drive a dispatcher. Probes are marshalled off-thread anyway.
        _timer = new System.Threading.Timer(_ => ProbeNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        var due = TimeSpan.FromSeconds(Math.Max(5, ProbeIntervalSeconds));
        _timer.Change(due, due);
    }
    public void Stop() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    public void Dispose() => _timer.Dispose();
    /// <summary>Seconds between automatic probes. Proven by <c>--stalltest</c> at a shorter value.</summary>
    public int ProbeIntervalSeconds { get; set; } = 60;

    /// <summary>Runs one probe. Called by the timer, by a wake-up, and by <c>--stalltest</c>.</summary>
    public void ProbeNow()
    {
        // A probe opens sockets and waits up to a second: never let two overlap, and never run it
        // on the UI thread.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Stats stats;
        try { stats = _stats(); }
        catch { Interlocked.Exchange(ref _busy, 0); return; }
        _ = Task.Run(() =>
        {
            try { Prober(stats.Port, OnProbed); }
            catch (Exception e) { Record("probe failed: " + e.Message); Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private void OnProbed(EngineShadow.Snapshot snap)
    {
        try
        {
            Last = snap;
            Stats stats = _stats();
            var before = _history.Count > 0 ? _history.Peek() : null;

            // "Growth" = the engine's LED inventory grew, i.e. the session is still coming up.
            if (_lastLedTotal < 0 || snap.LedTotal > _lastLedTotal) _probesSinceGrowth = 0;
            else _probesSinceGrowth++;
            _lastLedTotal = snap.LedTotal;

            bool moved = EngineShadow.AnyMotion(before, snap, stats.AnimatedDeviceNames());
            bool expectMotion = stats.EngineRunning && stats.EffectsRunning;
            if (moved) { _strikes = 0; try { MotionDetected?.Invoke(); } catch { } }
            else if (expectMotion) _strikes++;

            var verdict = EngineShadow.Decide(snap, before, expectMotion, stats.SecondsSinceSession,
                                              _strikes, _probesSinceGrowth);

            if (verdict == EngineShadow.Verdict.Wait && expectMotion && _strikes >= 1)
                verdict = EngineShadow.Verdict.Ok;   // one strike: keep watching, say nothing

            if (verdict != EngineShadow.Verdict.Ok)
                Record($"{DateTime.Now:HH:mm:ss} {verdict}: listen={snap.PortListening} " +
                       $"conns={snap.ClientConnections} leds={snap.LedTotal} zones={snap.ZoneTotal} " +
                       $"strikes={_strikes} devices={snap.Devices.Count}" +
                       (snap.Error.Length > 0 ? $" err={snap.Error}" : ""));

            _history.Enqueue(snap);
            while (_history.Count > 4) _history.Dequeue();

            if (verdict is EngineShadow.Verdict.Repair or EngineShadow.Verdict.Rebuild)
            {
                _strikes = 0;
                _probesSinceGrowth = 0;
                _act(verdict, snap);
            }
        }
        catch (Exception e) { Record("verdict failed: " + e.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Record(string line)
    {
        lock (_journalLock)
        {
            _journal.Add(line);
            if (_journal.Count > 40) _journal.RemoveAt(0);
        }
    }

    /// <summary>Newest-first last few verdicts, for the diagnostics line and the bug report.</summary>
    public string JournalText()
    {
        lock (_journalLock)
        {
            if (_journal.Count == 0) return "";
            return string.Join("\n", _journal);
        }
    }
}

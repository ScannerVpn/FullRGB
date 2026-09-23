namespace FullRGB.Setup;

/// <summary>
/// Crash-loop detector for the lighting engine. The watchdog already rebuilds a dead session;
/// what it cannot do alone is notice that the SAME failure keeps coming back every few minutes
/// and stop tearing the user's machine through endless repair cycles. This class counts full
/// engine replacements (not lost frames — those are one bad wake) inside a sliding window and
/// says "unstable" past the threshold. The UI turns that into safe mode: park the LEDs on a
/// calm static colour, show a banner, and retry with a growing delay.
///
/// Pure bookkeeping, no engine references — unit-testable without hardware.
/// </summary>
public sealed class EngineSafeMode
{
    private const int WindowMinutes = 10;
    private const int FailureThreshold = 3;
    private static readonly int[] BackoffSeconds = { 30, 60, 120, 300, 300 };

    private readonly Queue<long> _failures = new();
    private int _retryIndex;
    private long _enteredAtMs = -1;

    public bool Active { get; private set; }
    /// <summary>Profile the user had when safe mode was entered (restored on exit).</summary>
    public string? SuspendedProfile { get; private set; }

    /// <summary>Seconds until the next automatic retry (0 while inactive).</summary>
    public int SecondsUntilRetry
    {
        get
        {
            if (!Active) return 0;
            long elapsed = (Environment.TickCount64 - _enteredRetryAt) / 1000;
            int delay = BackoffSeconds[Math.Min(_retryIndex, BackoffSeconds.Length - 1)];
            int remain = delay - (int)elapsed;
            return Math.Max(0, remain);
        }
    }

    private long _enteredRetryAt;

    public void NoteEngineReplaced()
    {
        long now = Environment.TickCount64;
        lock (_failures)
        {
            _failures.Enqueue(now);
            while (_failures.Count > FailureThreshold) _failures.Dequeue();
        }
    }

    public void Reset()
    {
        lock (_failures) _failures.Clear();
    }

    /// <summary>True when <see cref="FailureThreshold"/> engine replacements happened within the window.</summary>
    public bool IsUnstable()
    {
        long now = Environment.TickCount64;
        lock (_failures)
        {
            while (_failures.Count > 0 && now - _failures.Peek() > WindowMinutes * 60_000) _failures.Dequeue();
            return _failures.Count >= FailureThreshold;
        }
    }

    public void Enter(string currentProfileName)
    {
        if (!Active)
        {
            Active = true;
            SuspendedProfile = string.IsNullOrWhiteSpace(currentProfileName) ? null : currentProfileName;
            _enteredAtMs = Environment.TickCount64;
            _retryIndex = 0;
            _enteredRetryAt = _enteredAtMs;
            Diag.AppLog.Warn($"engine safe mode ENTERED (profile parked: {SuspendedProfile ?? "-"})");
        }
        _enteredRetryAt = Environment.TickCount64;
    }

    /// <summary>Arms the next retry with the next backoff step. Returns the delay in seconds.</summary>
    public int ArmNextRetry()
    {
        if (_retryIndex < BackoffSeconds.Length - 1) _retryIndex++;
        _enteredRetryAt = Environment.TickCount64;
        return BackoffSeconds[_retryIndex];
    }

    public string? Exit()
    {
        if (!Active) return null;
        Active = false;
        _retryIndex = 0;
        _enteredAtMs = -1;
        lock (_failures) _failures.Clear();
        Diag.AppLog.Info("engine safe mode EXITED — session healthy again");
        return SuspendedProfile;
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FullRGB.Effects;

/// <summary>
/// Sub-millisecond sleep for the effect loop.
///
/// WHY THIS EXISTS: <c>Task.Delay</c>/<c>Thread.Sleep</c> round UP to the Windows scheduler tick
/// (~15.6 ms by default), so a 33 ms frame actually slept ~47 ms and the loop ran at ~20 fps
/// instead of 30 — measured, not guessed (`--fxtest` reported fps=20.4 while renderMs+ioMs was
/// under 0.1 ms, so all the missing time was in the sleep). `timeBeginPeriod(1)` is unreliable on
/// Windows 11 for background processes, and spin-waiting would burn a core in a tray app.
/// A high-resolution waitable timer (Win10 1803+) waits accurately while the thread stays parked.
/// </summary>
internal sealed class PreciseTimer : IDisposable
{
    private const uint CreateHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name,
                                                                uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
                                                IntPtr callback, IntPtr arg, bool resume);

    private readonly WaitHandle? _timer;

    /// <summary>True when the OS gave us a high-resolution timer (i.e. accurate waits).</summary>
    public bool IsHighResolution => _timer is not null;

    public PreciseTimer()
    {
        try
        {
            var h = CreateWaitableTimerExW(IntPtr.Zero, null, CreateHighResolution,
                                           TimerModifyState | Synchronize);
            if (h is { IsInvalid: false })
                _timer = new TimerWaitHandle(h);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>
    /// Waits <paramref name="ms"/> milliseconds, or returns early if <paramref name="cancel"/>
    /// fires. Returns false when cancellation ended the wait.
    /// </summary>
    public bool Wait(double ms, WaitHandle cancel)
    {
        if (ms <= 0) return !cancel.WaitOne(0);

        if (_timer is not null)
        {
            // negative = relative, unit = 100 ns
            long due = -(long)(ms * 10_000.0);
            if (due == 0) due = -1;
            if (SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                return WaitHandle.WaitAny(new[] { _timer, cancel }) == 0;
        }
        // fallback: coarse but correct
        return !cancel.WaitOne(TimeSpan.FromMilliseconds(ms));
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>WaitHandle has no public constructor taking a handle, so wrap it.</summary>
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
}

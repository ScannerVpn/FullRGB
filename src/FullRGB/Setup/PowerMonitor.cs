using System.Runtime.InteropServices;

namespace FullRGB.Setup;

/// <summary>
/// What the machine just did, decoded from the messages the OS actually sends.
/// </summary>
public enum PowerEvent { None, Suspend, Resume, Shutdown }

/// <summary>User-session transition (the OS reports these even when a power broadcast never arrives).</summary>
public enum SessionEvent { None, Logon, Logoff, Lock, Unlock, ConsoleDisconnect, ConsoleConnect }

/// <summary>
/// The OS notification side of "the lights were dark after sleep/hibernate".
///
/// WHY A REAL WINDOW AND NOT <c>SystemEvents.PowerModeChanged</c>: that managed event needs a
/// broadcast delivered to a hidden window created by .NET, and on Windows 11 it was reproduced as
/// NOT firing at all after a manual sleep/wake (dotnet/runtime#78162 — the OS never sends
/// PBT_APMRESUMEAUTOMATIC to it). Nothing in the old path fired, so the wake-up repair could not
/// run. Two independent native signals replaced it:
///
///  1. <c>WM_POWERBROADCAST</c> on a window WE create and register (<c>PBT_APMSUSPEND</c> /
///     <c>PBT_APMRESUMEAUTOMATIC</c> / <c>PBT_APMRESUMESUSPEND</c> / <c>PBT_APMRESUMECRITICAL</c>).
///     A window that exists and is registered gets these; a window the runtime forgot to register
///     does not.
///  2. <c>WM_WTSSESSION_CHANGE</c> via <c>WTSRegisterSessionNotification</c> — lock / unlock /
///     logon / logoff / console-connect. Those are delivered on their own channel and KEEP working
///     when no power broadcast arrives, which is what makes a machine that lost power (or a session
///     that was locked all night) recover too.
///
/// The window is top-level but never shown: HWND_MESSAGE (message-only) windows do NOT receive
/// broadcast messages, so a normal hidden window is required. It is created on the WPF UI thread,
/// whose dispatcher pumps the messages, so no extra loop or thread is needed.
/// </summary>
public sealed class PowerMonitor : IDisposable
{
    // ---- window messages ----
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_WTSSESSION_CHANGE = 0x02B1;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;

    // ---- WM_POWERBROADCAST events ----
    private const int PBT_APMRESUMECRITICAL = 0x0006;    // power was CUT and came back
    private const int PBT_APMRESUMESUSPEND = 0x0007;     // wake with user input
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;   // wake without it

    // ---- WM_WTSSESSION_CHANGE reasons ----
    private const int WTS_CONSOLE_CONNECT = 0x1;
    private const int WTS_CONSOLE_DISCONNECT = 0x2;
    private const int WTS_SESSION_LOGOFF = 0x5;
    private const int WTS_SESSION_LOGON = 0x6;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int WTS_SESSION_UNLOCK = 0x8;

    private const int NOTIFY_FOR_THIS_SESSION = 0;
    private const int AF_INET = 2;

    private readonly WndProcDelegate _proc;   // pinned field: a collected delegate kills the window
    private IntPtr _hwnd;
    private bool _registered;

    /// <summary>The machine is going to sleep (flush before the freeze).</summary>
    public event Action? Suspended;
    /// <summary>The machine came back from sleep, hibernate or a power cut.</summary>
    public event Action? Resumed;
    /// <summary>The user session was locked / unlocked / connected / disconnected.</summary>
    public event Action<SessionEvent>? SessionChanged;
    /// <summary>Windows is ending the session (logoff/shutdown).</summary>
    public event Action? SessionEnding;

    /// <summary>False when the window could not be created; callers then fall back to the heartbeat.</summary>
    public bool IsSupported => _hwnd != IntPtr.Zero;

    /// <summary>Last decoded event, for diagnostics and for log lines.</summary>
    public string LastEvent { get; private set; } = "none";

    public PowerMonitor()
    {
        _proc = WindowProc;
        try
        {
            var hInstance = GetModuleHandle(null);
            string cls = "FullRGB-PowerMonitor-" + Guid.NewGuid().ToString("N")[..8];
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = cls,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                // 1410 = class already exists (harmless); anything else means no window.
                if (err != 1410) return;
            }
            // Top-level (parent = 0) and never shown: broadcasts reach it, HWND_MESSAGE windows
            // would not. Zero size, no styles: nothing to lay out, nothing to steal focus.
            _hwnd = CreateWindowEx(0, cls, "FullRGB power monitor", 0, 0, 0, 0, 0,
                                   IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;
            _registered = WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);
        }
        catch { /* no monitor: the heartbeat in MainWindow still detects a long freeze */ }
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_POWERBROADCAST:
                    switch ((int)wParam)
                    {
                        case PBT_APMRESUMEAUTOMATIC:
                        case PBT_APMRESUMESUSPEND:
                        case PBT_APMRESUMECRITICAL:
                            LastEvent = $"resume(0x{(int)wParam:X4})";
                            Resumed?.Invoke();
                            return (IntPtr)1;
                        default:
                            // PBT_APMSUSPEND (0x0004) and friends: the OS tells us before the freeze.
                            if ((int)wParam == 0x0004)
                            {
                                LastEvent = "suspend";
                                Suspended?.Invoke();
                                return (IntPtr)1;
                            }
                            break;
                    }
                    break;

                case WM_WTSSESSION_CHANGE:
                    var ev = DecodeSessionEvent((int)wParam);
                    if (ev != SessionEvent.None)
                    {
                        LastEvent = "session:" + ev;
                        SessionChanged?.Invoke(ev);
                        if (ev is SessionEvent.Logoff or SessionEvent.ConsoleDisconnect) SessionEnding?.Invoke();
                    }
                    return IntPtr.Zero;

                case WM_CLOSE:
                    return IntPtr.Zero;    // never let anything close the monitor out from under us
            }
        }
        catch { /* a subscriber must never take the message loop down */ }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Pure: which power event a raw WM_POWERBROADCAST wParam describes (--rendertest §43).</summary>
    public static PowerEvent DecodePowerEvent(int wParam) => wParam switch
    {
        PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND or PBT_APMRESUMECRITICAL => PowerEvent.Resume,
        0x0004 => PowerEvent.Suspend,
        0x0005 => PowerEvent.Shutdown,
        _ => PowerEvent.None,
    };

    /// <summary>Pure: which session event a raw WM_WTSSESSION_CHANGE wParam describes (--rendertest §43).</summary>
    public static SessionEvent DecodeSessionEvent(int wParam) => wParam switch
    {
        WTS_CONSOLE_CONNECT => SessionEvent.ConsoleConnect,
        WTS_CONSOLE_DISCONNECT => SessionEvent.ConsoleDisconnect,
        WTS_SESSION_LOGOFF => SessionEvent.Logoff,
        WTS_SESSION_LOGON => SessionEvent.Logon,
        WTS_SESSION_LOCK => SessionEvent.Lock,
        WTS_SESSION_UNLOCK => SessionEvent.Unlock,
        _ => SessionEvent.None,
    };

    /// <summary>
    /// Windows terminal-session id of a process (not the same as the process id). Used to notice a
    /// LOGON: a freshly logged-on session is the moment a stale engine — one left behind by an
    /// earlier session that could not be killed, e.g. after a power cut — becomes visible.
    /// Returns -1 when it cannot be read.
    /// </summary>
    public static int SessionIdOfProcess(int processId)
    {
        try
        {
            return ProcessIdToSessionId(processId, out uint sid) ? (int)sid : -1;
        }
        catch { return -1; }
    }

    /// <summary>Terminal-session id of THIS process (its own session), or -1.</summary>
    public static int CurrentSessionId() => SessionIdOfProcess(Environment.ProcessId);

    public void Dispose()
    {
        try
        {
            if (_hwnd != IntPtr.Zero && _registered) WTSUnRegisterSessionNotification(_hwnd);
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        }
        catch { }
        _hwnd = IntPtr.Zero;
        _registered = false;
    }

    // ---------- native ----------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int dwProcessId, out uint pSessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}

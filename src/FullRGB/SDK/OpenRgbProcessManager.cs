using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace FullRGB.SDK;

/// <summary>
/// Owns the bundled OpenRGB.exe process: starts it with the SDK server flags,
/// waits for the port, stops it on shutdown. Flags are exactly
/// "--server --server-port N" (any other spelling makes OpenRGB abort at exit).
/// Never kills a server that is already serving the port — a second app instance
/// would otherwise cut the lighting of the first.
/// The engine is started with THIS process's token: FullRGB does not self-elevate, because
/// every HID device (board headers, Commander Core, ARGB fans) works as a normal user.
/// If the user launched FullRGB as administrator, OpenRGB inherits that and RAM/GPU appear too.
/// </summary>
public sealed class OpenRgbProcessManager : IDisposable
{
    private Process? _proc;
    public string ExePath { get; }
    public int Port { get; }

    /// <summary>True when we attached to a server that was already running (we must not kill it).</summary>
    public bool AttachedToExisting { get; private set; }

    /// <summary>True when the engine was launched through the elevated Scheduled Task.</summary>
    public bool StartedViaTask { get; private set; }

    /// <summary>Why the task path was not used, when a task exists but did not work.</summary>
    public string? TaskStartError { get; private set; }

    public OpenRgbProcessManager(string exePath, int port = 6742)
    {
        ExePath = exePath;
        Port = port;
    }

    public static string DefaultExePath()
    {
        // Preferred: the engine embedded inside FullRGB.exe. Unpacked once into LocalAppData, so
        // the shipped app is a single file with no OpenRGB folder beside it.
        if (SDK.EngineBundle.IsEmbedded)
        {
            try
            {
                var extracted = SDK.EngineBundle.EnsureExtracted();
                SDK.EngineBundle.PruneOldVersions(extracted);
                return extracted;
            }
            catch
            {
                // Fall through to the on-disk layouts below rather than dying: a locked or full
                // LocalAppData must not make the app unusable when a vendor folder is present.
            }
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidate = Path.Combine(baseDir, "vendor", "OpenRGB", "OpenRGB Windows 64-bit", "OpenRGB.exe");
        if (File.Exists(candidate)) return candidate;
        var flat = Path.Combine(baseDir, "vendor", "OpenRGB", "OpenRGB.exe");
        if (File.Exists(flat)) return flat;
        return candidate; // will fail later with a clear error
    }

    public bool IsRunning => _proc is { HasExited: false } || AttachedToExisting;

    /// <summary>Starts OpenRGB with our own token and waits until the SDK port accepts TCP.</summary>
    public async Task StartAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_proc is { HasExited: false }) return;

        // Someone is already serving the SDK port: reuse it instead of fighting over the hardware.
        if (await PortOpenAsync().ConfigureAwait(false))
        {
            AttachedToExisting = true;
            return;
        }
        AttachedToExisting = false;

        if (!File.Exists(ExePath))
            throw new FileNotFoundException($"OpenRGB.exe not found at {ExePath}");

        KillOrphans();

        // Preferred path: an elevated Scheduled Task registered once by the user. The engine then
        // gets SMBus access (RGB RAM appears) while THIS process stays a normal user and no UAC
        // prompt is shown. Falls through to a plain launch if the task is missing or fails.
        //
        // MatchesInstall, not IsRegistered: the task stores an ABSOLUTE exe path, so a task left
        // behind by another install (dist8 vs dist9, or a moved folder) would silently start the
        // WRONG engine — or none at all.
        if (Setup.EngineTask.MatchesInstall(ExePath))
        {
            if (Setup.EngineTask.Run(out var taskError))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var limit = timeout ?? TimeSpan.FromSeconds(60);
                while (sw.Elapsed < limit)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await PortOpenAsync().ConfigureAwait(false))
                    {
                        // We did not create the process object, and an elevated engine cannot be
                        // killed from here anyway: treat it as external so Stop() leaves it alone.
                        AttachedToExisting = true;
                        StartedViaTask = true;
                        return;
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                TaskStartError = $"engine task started but port {Port} never opened";
            }
            else
            {
                TaskStartError = taskError;
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"--server --server-port {Port}",
            // No shell execute + no window: UseShellExecute=true was only needed for the "runas"
            // verb, and it also made OpenRGB flash a window. CreateNoWindow keeps it invisible.
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "",
        };

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start OpenRGB");

        var proc = _proc;
        var deadlineSw = System.Diagnostics.Stopwatch.StartNew();
        var waitLimit = timeout ?? TimeSpan.FromSeconds(60);
        try
        {
            while (deadlineSw.Elapsed < waitLimit)
            {
                ct.ThrowIfCancellationRequested();
                proc.Refresh();
                if (proc.HasExited)
                    throw new InvalidOperationException($"OpenRGB exited early (code {proc.ExitCode}). See {LogDir()}.");
                if (await PortOpenAsync().ConfigureAwait(false)) return;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Timeout / cancel / early exit: don't orphan the engine holding the port.
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            try { proc.Dispose(); } catch { }
            if (ReferenceEquals(_proc, proc)) _proc = null;
            throw;
        }
        try { if (!proc.HasExited) proc.Kill(true); } catch { }
        try { proc.Dispose(); } catch { }
        if (ReferenceEquals(_proc, proc)) _proc = null;
        throw new TimeoutException($"OpenRGB SDK port {Port} did not open within the timeout");
    }

    /// <summary>Restarts the engine after a crash: clears state, kills leftovers, starts fresh.</summary>
    public async Task RestartAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Stop();
        AttachedToExisting = false;
        StartedViaTask = false;
        TaskStartError = null;
        KillOrphans();
        // Wait for the OLD server's port to actually close before starting a new one,
        // otherwise StartAsync attaches to the dying engine (shutdown/startup race).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            ct.ThrowIfCancellationRequested();
            if (!await PortOpenAsync().ConfigureAwait(false)) break;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        await Task.Delay(600, ct).ConfigureAwait(false);
        await StartAsync(timeout, ct).ConfigureAwait(false);
    }

    public async Task<bool> PortOpenAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        try
        {
            using var t = new TcpClient();
            await t.ConnectAsync("127.0.0.1", Port, cts.Token).ConfigureAwait(false);
            return t.Connected;
        }
        catch { return false; }
    }

    /// <summary>Kills leftover OpenRGB processes launched from OUR vendor folder only.</summary>
    public void KillOrphans()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("OpenRGB"))
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                // Unknown path = elevated process we cannot inspect; leave it alone rather than
                // killing a server another instance (or the user) is relying on.
                if (path is not null && string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { p.Kill(true); } catch { }
                }
            }
        }
        catch { /* best effort */ }
    }

    public void Stop()
    {
        // Only stop a server we started ourselves.
        if (!AttachedToExisting)
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        }
        _proc?.Dispose();
        _proc = null;
    }

    /// <summary>
    /// Best-effort stop that also kills an elevated engine started via the Scheduled Task.
    /// Used when the user opts in to \"close engine with the app\" — otherwise closing
    /// FullRGB would leave OpenRGB.exe lingering and the user would have to kill it from
    /// Task Manager. This costs one UAC prompt only when the engine was actually elevated;
    /// when the engine was ours or no engine is running it is the same as <see cref=\"Stop\"/>.
    /// </summary>
    public void StopIncludingElevated()
    {
        Stop();
        // _proc == null but the task's engine (elevated) is still serving the port: we left
        // it alive intentionally before (AttachedToExisting). If the user asked to close with
        // the app, kill that too — but only if something is still listening.
        try
        {
            if (PortOpenSync())
                Setup.EngineTask.StopElevatedEngine(out _);
        }
        catch { }
    }

    private bool PortOpenSync()
    {
        try
        {
            using var t = new TcpClient();
            var ar = t.BeginConnect("127.0.0.1", Port, null, null);
            bool ok = ar.AsyncWaitHandle.WaitOne(500);
            if (!ok) return false;
            try { t.EndConnect(ar); } catch { return false; }
            return t.Connected;
        }
        catch { return false; }
    }

    public string LogDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB", "logs");

    public string ReadLatestLog()
    {
        try
        {
            var dir = new DirectoryInfo(LogDir());
            var latest = dir.GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (latest is null) return "";
            using var fs = latest.OpenRead();
            var buf = new byte[Math.Min(8192, fs.Length)];
            fs.Seek(-buf.Length, SeekOrigin.End);
            fs.ReadExactly(buf);
            return System.Text.Encoding.UTF8.GetString(buf);
        }
        catch { return ""; }
    }

    /// <summary>True when the latest engine log says SMBus/I2C init failed (RAM/GPU will be missing).</summary>
    public bool LastRunHadSmbusFailure()
    {
        var log = ReadLatestLog();
        return log.Contains("Permission Denied", StringComparison.OrdinalIgnoreCase)
            || log.Contains("PawnIO initialization aborted", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Stop();
}

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Relaunches the current process elevated and exits the non-elevated instance.</summary>
    public static void RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = true,
            Verb = "runas",
        };
        Process.Start(psi);
        Environment.Exit(0);
    }
}

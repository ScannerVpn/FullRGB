using System.Diagnostics;
using System.IO;

namespace FullRGB.Setup;

/// <summary>
/// Registers the bundled OpenRGB engine as an elevated, on-demand Scheduled Task so RGB RAM
/// works while FullRGB itself keeps running as a normal user.
///
/// WHY (proven from the engine's own log on this rig):
///   unelevated:  "Start PawnIO: SmbusI801.bin" -> "ERROR: Permission Denied,
///                 PawnIO initialization aborted"   => 2 controllers (board + cooler)
///   elevated:    "Start PawnIO: SmbusI801.bin" -> "PawnIO initialized successfully"
///                 + "[ENE DRAM] Registering RGB controller" x2  => 4 controllers
/// RGB DIMMs sit on the SMBus, SMBus needs the PawnIO kernel driver, and PawnIO only opens from
/// an elevated process. That is a Windows constraint, not a FullRGB bug.
///
/// The task is registered WITHOUT a trigger and with RunLevel=Highest:
///   * creating it prompts for UAC exactly once,
///   * `schtasks /Run` afterwards starts the engine elevated with NO further prompt,
///   * FullRGB.exe itself stays asInvoker and never self-elevates.
/// </summary>
public static class EngineTask
{
    public const string TaskName = "FullRGB-Engine";

    /// <summary>
    /// PowerShell that registers the task. Uses the ScheduledTasks module rather than
    /// `schtasks /Create`, because every schtasks schedule type needs a start date/time and the
    /// accepted date FORMAT is locale-dependent — a trigger-less task avoids that trap entirely
    /// and is exactly what "run only on demand" means.
    /// </summary>
    public static string BuildRegisterScript(string exePath, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        string dir = Path.GetDirectoryName(exePath) ?? "";
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // PowerShell single-quoted strings: only ' needs escaping (doubled).
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        // DOMAIN\user (not bare username): bare names fail on domain/AzureAD/MSA accounts
        // with "No mapping between account names and SIDs".
        string user = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USERDOMAIN"))
            ? Environment.UserName
            : Environment.GetEnvironmentVariable("USERDOMAIN") + "\\" + Environment.UserName;

        return $@"$ErrorActionPreference = 'Stop'
$action = New-ScheduledTaskAction -Execute {Q(exePath)} `
    -Argument '--server --server-port {port}' -WorkingDirectory {Q(dir)}
# Highest = the engine runs elevated, which is the whole point (SMBus/PawnIO for RGB RAM).
$principal = New-ScheduledTaskPrincipal -UserId {Q(user)} `
    -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName {Q(TaskName)} -Action $action -Principal $principal `
    -Settings $settings -Description 'Runs the FullRGB lighting engine with SMBus access.' -Force | Out-Null
exit 0
";
    }

    public static string BuildUnregisterScript()
        => $@"$ErrorActionPreference = 'SilentlyContinue'
Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false
exit 0
";

    /// <summary>True when the elevated engine task exists (cheap: one schtasks query).</summary>
    public static bool IsRegistered() => QueryXml() is not null;

    /// <summary>
    /// The exe path the task will actually run, or null when the task does not exist.
    /// Needed because the task is registered with an ABSOLUTE path: if the user later runs a
    /// different install (or the folder moves), the task would silently launch the old engine —
    /// or fail once that folder is gone.
    /// </summary>
    public static string? RegisteredExePath()
    {
        var xml = QueryXml();
        return xml is null ? null : ParseCommand(xml);
    }

    /// <summary>
    /// Pulls the &lt;Command&gt; value out of a task XML document. Split out from the schtasks call
    /// so it is unit-testable (`--rendertest`) without touching the Task Scheduler.
    /// Uses XML parsing (not substring): namespaces/whitespace/attributes/entities break naive search.
    /// </summary>
    internal static string? ParseCommand(string xml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var cmd = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command");
            if (cmd is null) return null;
            var raw = System.Net.WebUtility.HtmlDecode(cmd.Value).Trim();
            // Strip one layer of surrounding quotes (single or double).
            if (raw.Length >= 2 && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
                raw = raw[1..^1];
            return raw.Trim();
        }
        catch { return null; }
    }

    /// <summary>True when a task exists AND points at this installation's engine.</summary>
    public static bool MatchesInstall(string exePath)
    {
        var registered = RegisteredExePath();
        return registered is not null
            && string.Equals(Path.GetFullPath(registered), Path.GetFullPath(exePath),
                             StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryXml()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /XML",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;

            // schtasks /XML DECLARES encoding="UTF-16" in the prolog but, when its output is
            // redirected, actually writes single-byte text (verified with od on this machine).
            // So decode the raw bytes and pick whichever interpretation contains real markup —
            // forcing Encoding.Unicode produced garbage and made the task look unregistered.
            using var ms = new MemoryStream();
            var copyTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
            // CopyTo blocks before WaitForExit: a hung schtasks would hang us forever.
            // Wait async-read + process exit together, with a deadline.
            if (!copyTask.Wait(TimeSpan.FromSeconds(8))) { try { p.Kill(); } catch { } return null; }
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
            if (!p.HasExited || p.ExitCode != 0) return null;

            var bytes = ms.ToArray();
            foreach (var enc in new System.Text.Encoding[]
                     { System.Text.Encoding.UTF8, System.Text.Encoding.Unicode, System.Text.Encoding.Default })
            {
                string text = enc.GetString(bytes);
                if (text.Contains("<Command>", StringComparison.Ordinal)) return text;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Creates (or replaces) the elevated task. Shows ONE UAC prompt. Returns false if the user
    /// declined or the registration failed; <paramref name="error"/> then carries the reason.
    /// </summary>
    public static bool Register(string exePath, int port, out string error)
        => RunElevatedScript(BuildRegisterScript(exePath, port), out error) && IsRegistered();

    public static bool Unregister(out string error)
        => RunElevatedScript(BuildUnregisterScript(), out error);

    /// <summary>
    /// Kills a running engine that we cannot touch from a normal-user process.
    /// Needed when the user turns the RAM option OFF: the elevated engine started by the task
    /// keeps running (and keeps serving the SDK port), so FullRGB would just re-attach to it and
    /// the setting would look like it had no effect. Costs one UAC prompt, only on that path.
    /// </summary>
    public static bool StopElevatedEngine(out string error)
        => RunElevatedScript(
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            "Stop-Process -Name OpenRGB -Force\r\n" +
            "exit 0\r\n", out error);

    /// <summary>
    /// Starts the engine through the task. Needs NO elevation and shows no prompt: the Task
    /// Scheduler service launches it at the task's own (highest) level.
    /// Verifies the task points at THIS install first: otherwise schtasks silently launches
    /// a stale path after a folder move.
    /// </summary>
    public static bool Run(out string error)
    {
        error = "";
        try
        {
            var exe = SDK.OpenRgbProcessManager.DefaultExePath();
            if (!MatchesInstall(exe)) { error = "engine task points at another install; re-register it"; return false; }
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) { error = "could not start schtasks"; return false; }
            // Async reads: ReadToEnd-before-WaitForExit deadlocks when output fills the pipe.
            var stdTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } error = "schtasks /Run did not finish"; return false; }
            string std = stdTask.Result;
            string err = errTask.Result;
            if (p.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(err) ? std.Trim() : err.Trim();
                if (string.IsNullOrWhiteSpace(error)) error = $"schtasks exit {p.ExitCode}";
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes the script to a temp file and runs it via `powershell -File` with the runas verb.
    /// A temp FILE (not -Command) keeps quoting out of the picture entirely.
    /// </summary>
    internal static bool RunElevatedScript(string script, out string error)
    {
        error = "";
        string path = Path.Combine(Path.GetTempPath(), $"fullrgb-task-{Guid.NewGuid():N}.ps1");
        try
        {
            // UTF-8 WITH BOM: powershell.exe (5.1) reads a BOM-less file as ANSI and would mangle
            // any non-ASCII path.
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"",
                UseShellExecute = true,   // required for the runas verb
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) { error = "could not start powershell"; return false; }
            p.WaitForExit(120_000);
            if (!p.HasExited) { error = "the elevated helper did not finish"; return false; }
            if (p.ExitCode != 0) { error = $"exit code {p.ExitCode}"; return false; }
            return true;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            error = "cancelled";   // ERROR_CANCELLED: the user declined the UAC prompt
            return false;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

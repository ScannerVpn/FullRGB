using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FullRGB.Diag;

/// <summary>
/// One-click bug-report bundle: a zip with everything a hardware issue needs and nothing that
/// needs explaining — system facts, the support matrix (VID:PID + why), the engine's controller
/// list, the latest engine log, OUR app log and the current settings.
///
/// The user picks the target with a normal Save dialog; the build itself is pure .NET zip so the
/// same code can run headless (<c>--export-diagnostics=path</c>) for CI and support tickets.
/// </summary>
public static class DiagnosticsExport
{
    /// <summary>Builds the zip synchronously. Returns the file written. Never throws unless the
    /// target path is unwritable. Callers supply the support-matrix lines (the GUI passes the
    /// live controller list; headless passes null for engine-offline reporting).</summary>
    public static string Build(string targetPath, Func<IEnumerable<string>> supportMatrixLines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        AddText(zip, "system.txt", SystemText());
        AddText(zip, "controllers.txt", ControllersText());
        AddText(zip, "support-matrix.txt", string.Join(Environment.NewLine, supportMatrixLines()));
        AddText(zip, "app-log.txt", AppLog.ReadFileTail());
        AddText(zip, "engine-log.txt", EngineLogTail());
        AddSettings(zip, "settings.json");
        AddText(zip, "report.txt", HardwareReportText());
        return targetPath;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static void AddSettings(ZipArchive zip, string name)
    {
        try
        {
            var src = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB", "settings.json");
            if (!File.Exists(src)) return;
            zip.CreateEntryFromFile(src, name, CompressionLevel.Optimal);
        }
        catch { }
    }

    /// <summary>Static system facts: versions, launch path, engine task, PawnIO, elevation.</summary>
    public static string SystemText()
    {
        var sb = new StringBuilder();
        string exe = Environment.ProcessPath ?? "?";
        string engineExe = "";
        try { engineExe = SDK.OpenRgbProcessManager.DefaultExePath(); } catch { }
        sb.AppendLine($"Generated      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version    : {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"App path       : {exe}");
        sb.AppendLine($"64-bit process : {Environment.Is64BitProcess}");
        sb.AppendLine($"Elevated       : {SDK.Elevation.IsElevated}");
        sb.AppendLine($"OS             : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"OS 64-bit      : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Machine        : {Environment.MachineName} ({Environment.UserDomainName}\\{Environment.UserName})");
        sb.AppendLine($"Runtime        : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        sb.AppendLine($"Engine path    : {engineExe}");
        try
        {
            if (File.Exists(engineExe))
            {
                var vi = FileVersionInfo.GetVersionInfo(engineExe);
                sb.AppendLine($"Engine version : {vi.ProductVersion ?? vi.FileVersion ?? "??"}");
                sb.AppendLine($"Engine size    : {new FileInfo(engineExe).Length / 1048576.0:0.#} MB");
            }
        }
        catch { }
        sb.AppendLine($"Engine embedded: {SDK.EngineBundle.IsEmbedded} ({(SDK.EngineBundle.IsEmbedded ? SDK.EngineBundle.EmbeddedSize() / 1048576.0 : 0):0.#} MB)");
        try
        {
            sb.AppendLine($"Engine task    : registered={Setup.EngineTask.IsRegistered()} " +
                          $"matchesThisInstall={Setup.EngineTask.MatchesInstall(engineExe)}");
        }
        catch { }
        try
        {
            sb.AppendLine($"PawnIO         : {Setup.DependencyManager.IsPawnIoInstalled()}");
        }
        catch { }
        sb.AppendLine($"Screens        : {System.Windows.Forms.Screen.AllScreens.Length}");
        return sb.ToString();
    }

    /// <summary>Engine controller dump — set from the live client; empty when the engine is offline.</summary>
    public static string ControllersText(IEnumerable<SDK.RgbController>? controllers = null)
    {
        var sb = new StringBuilder();
        var list = (controllers ?? Array.Empty<SDK.RgbController>()).ToList();
        if (list.Count == 0) { sb.AppendLine("engine offline (no controllers)"); return sb.ToString(); }
        sb.AppendLine($"count={list.Count}");
        foreach (var c in list)
        {
            sb.AppendLine($"[{c.Index}] {c.Name}");
            sb.AppendLine($"     vendor={c.Vendor} type={c.Kind} leds={c.LedCount}");
            sb.AppendLine($"     location={c.Location}");
            sb.AppendLine($"     zones={c.Zones.Count}");
            foreach (var z in c.Zones)
                sb.AppendLine($"     zone[{z.Index}] name={z.Name} leds={z.LedsCount} range={z.LedsMin}-{z.LedsMax}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The support matrix as one line per device (state | label | VID:PID | reason), shared by
    /// the diagnostics zip, the --export-diagnostics headless verb and the UI button. Without
    /// live controllers the report still lists every USB device as "unknown" — honest, because
    /// the engine being offline means nothing is known about drivers.
    /// </summary>
    public static IEnumerable<string> SupportMatrixText(IEnumerable<SDK.RgbController>? controllers = null)
    {
        var list = new List<string>();
        try
        {
            bool connected = controllers is not null;
            bool smbusFailed = false;
            try
            {
                smbusFailed = new SDK.OpenRgbProcessManager(SDK.OpenRgbProcessManager.DefaultExePath())
                    .LastRunHadSmbusFailure();
            }
            catch { }
            foreach (var r in SupportMatrix.Build(controllers ?? Enumerable.Empty<SDK.RgbController>(),
                                                  smbusFailed, connected))
            {
                if (r.State == SupportState.NotLighting) continue;
                list.Add($"{r.State,-14} | {r.Label} | {r.VidPid} | {r.Reason}");
            }
            list.Add("");
            list.Add("USB/HID inventory:");
            foreach (var d in UsbScan.Scan())
                list.Add($"  {d.VidPid}  {d.DeviceClass,-12} {d.Label}");
        }
        catch (Exception e)
        {
            list.Add($"(support matrix failed: {e.Message})");
        }
        return list;
    }

    private static string HardwareReportText()
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine(SystemText());
            sb.AppendLine(ControllersText());
        }
        catch { }
        return sb.ToString();
    }

    private static string EngineLogTail()
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB", "logs"));
            var latest = dir.GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (latest is null) return "(no engine log found)";
            using var fs = latest.OpenRead();
            int take = (int)Math.Min(256 * 1024, fs.Length);
            var buf = new byte[take];
            fs.Seek(-take, SeekOrigin.End);
            fs.ReadExactly(buf);
            return Encoding.UTF8.GetString(buf);
        }
        catch (Exception e)
        {
            return $"(engine log unavailable: {e.Message})";
        }
    }
}

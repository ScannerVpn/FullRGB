using System.IO;
using System.Text;

namespace FullRGB.Diag;

/// <summary>
/// Tiny rolling app log. Purpose: after a crash or a "why did the lights stop" report there must
/// be an event trail that is OURS (the engine has its own logs), exportable as part of the
/// diagnostics zip in one click. Kept deliberately dependency-free: a ring buffer in memory plus
/// a per-day text file under %APPDATA%\FullRGB\logs.
/// </summary>
public static class AppLog
{
    private const int RingSize = 600;
    private static readonly object Lock = new();
    private static readonly Queue<string> Ring = new(RingSize);

    public static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB", "logs");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR", msg);

    public static void Exception(string where, Exception e) => Write("ERR", $"{where}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        try
        {
            lock (Lock)
            {
                Ring.Enqueue(line);
                while (Ring.Count > RingSize) Ring.Dequeue();
            }
        }
        catch { }
        try
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            // keep the folder from growing forever: drop app logs older than 14 days
            foreach (var f in new DirectoryInfo(LogDir).GetFiles("app-*.log"))
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))
                    try { f.Delete(); } catch { }
        }
        catch { }
    }

    /// <summary>All lines currently held in memory (diagnostics export + UI).</summary>
    public static IReadOnlyList<string> Snapshot()
    {
        lock (Lock) return Ring.ToArray();
    }

    /// <summary>Text of today's (and yesterday's) app log files, capped.</summary>
    public static string ReadFileTail(int maxBytes = 128 * 1024)
    {
        try
        {
            var files = new DirectoryInfo(LogDir).GetFiles("app-*.log")
                .OrderByDescending(f => f.Name).Take(2).ToList();
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                using var fs = f.OpenRead();
                int take = (int)Math.Min(maxBytes, fs.Length);
                var buf = new byte[take];
                fs.Seek(-take, SeekOrigin.End);
                fs.ReadExactly(buf);
                sb.Append(Encoding.UTF8.GetString(buf));
                if (sb.Length >= maxBytes) break;
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"(app log unavailable: {e.Message})";
        }
    }
}

using System.IO;

namespace FullRGB.Diag;

/// <summary>
/// "Did the last run end cleanly?" marker.
///
/// The rolling app log only records what the code CHOOSES to log, so a hard crash — an unhandled
/// exception in a UI event handler, a kill from Task Manager, a dead process after a power event —
/// used to leave no trace anywhere: not in <see cref="AppLog"/>, not in the Windows event log.
/// That is exactly how the v1.6.0 Hardware-tab crash shipped: the tab terminated the process and
/// the next launch had nothing to report.
///
/// A marker file written at startup and removed on a clean exit turns "the app vanished" into a
/// fact the next session can state out loud.
/// </summary>
public static class SessionMarker
{
    /// <summary>Test seam: redirects the marker file. Null = the real %APPDATA% location.</summary>
    internal static string? PathOverride { get; set; }

    private static string MarkerPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FullRGB", "session.lock");

    /// <summary>
    /// Contents of the previous run's marker when that run never reached a clean exit, else null.
    /// Read once by <see cref="Begin"/>; safe to consult for the rest of the session.
    /// </summary>
    public static string? PreviousUncleanRun { get; private set; }

    /// <summary>Records this run. Call EARLY on startup, before anything can fail.</summary>
    public static void Begin()
    {
        PreviousUncleanRun = null;
        try
        {
            // Capture the previous marker BEFORE overwriting it - that text is the whole point.
            if (File.Exists(MarkerPath))
                PreviousUncleanRun = File.ReadAllText(MarkerPath).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath,
                $"{DateTime.Now:o} pid={Environment.ProcessId} version={Update.AppUpdater.CurrentVersion()}");
        }
        catch { /* a marker we cannot write must never block startup */ }
    }

    /// <summary>
    /// True when the marker file is still present, i.e. the previous run never exited cleanly.
    /// Readable WITHOUT <see cref="Begin"/> on purpose: the updater's rollback check runs before
    /// anything else has touched the marker, and must see the previous run's state.
    /// </summary>
    public static bool WasPreviousRunUnclean()
    {
        try { return File.Exists(MarkerPath); } catch { return false; }
    }

    /// <summary>Clears the marker: this run is exiting on purpose.</summary>
    public static void End()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
    }
}

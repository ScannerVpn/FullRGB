using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace FullRGB.SDK;

/// <summary>
/// The lighting engine SHIPS INSIDE FullRGB.exe.
///
/// The whole portable OpenRGB tree (exe + Qt runtime + SMBus PawnIO modules) is zipped at build
/// time and embedded as a managed resource, so what we hand the user is one file. On first run the
/// bundle is unpacked into LocalAppData and reused from there; nothing is downloaded, nothing is
/// installed system-wide, and no separate program appears next to the app.
///
/// The extraction folder is keyed by the bundle's SHA-256, which gives two things for free:
///   * a new engine version lands in a NEW folder instead of half-overwriting the old one
///   * the same bundle is unpacked exactly once, ever (a marker file proves completion)
/// </summary>
public static class EngineBundle
{
    /// <summary>Resource name set by the csproj ZipDirectory/EmbeddedResource pair.</summary>
    public const string ResourceName = "FullRGB.engine.zip";

    private static readonly object Gate = new();
    private static string? _cachedExe;

    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullRGB", "engine");

    /// <summary>True when this build actually carries the engine inside it.</summary>
    public static bool IsEmbedded =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>Size of the embedded bundle in bytes, or 0 when this build has none.</summary>
    public static long EmbeddedSize()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            return s?.Length ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Returns the path of the unpacked OpenRGB.exe, unpacking on first call.
    /// Throws only if this build has no embedded bundle at all.
    /// </summary>
    public static string EnsureExtracted()
    {
        if (_cachedExe is not null && File.Exists(_cachedExe)) return _cachedExe;

        // Cross-process gate: two app instances starting together must not delete
        // each other's half-extracted folder.
        using var mtx = new Mutex(false, @"Local\FullRGB_EngineExtract");
        bool owned = false;
        try { owned = mtx.WaitOne(TimeSpan.FromSeconds(60)); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new TimeoutException("timed out waiting for engine extraction");
        try
        {
            return EnsureExtractedLocked();
        }
        finally { try { if (owned) mtx.ReleaseMutex(); } catch { } }
    }

    private static string EnsureExtractedLocked()
    {
        if (_cachedExe is not null && File.Exists(_cachedExe)) return _cachedExe;

            var asm = Assembly.GetExecutingAssembly();
            using var res = asm.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException(
                    $"this build has no embedded engine ({ResourceName})");

            // Copy to a seekable buffer: the manifest stream is not guaranteed seekable,
            // and we need two passes (hash, then unzip).
            using var mem = new MemoryStream();
            res.CopyTo(mem);
            string hash;
            using (var sha = SHA256.Create())
                hash = Convert.ToHexString(sha.ComputeHash(mem.ToArray()))[..12];

            var dir = Path.Combine(RootDir, hash);
            var exe = Path.Combine(dir, "OpenRGB.exe");
            var marker = Path.Combine(dir, ".complete");

            if (File.Exists(marker) && File.Exists(exe))
            {
                _cachedExe = exe;
                return exe;
            }

            // Extract to a temp dir then ATOMICALLY rename: a concurrent/older instance
            // never sees a half-extracted folder, and we never delete another's work.
            var tmpDir = dir + ".tmp-" + Environment.ProcessId;
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
            Directory.CreateDirectory(tmpDir);

            mem.Position = 0;
            using (var zip = new ZipArchive(mem, ZipArchiveMode.Read, leaveOpen: true))
            {
                string dirFull = Path.GetFullPath(dir);
                string dirPrefix = dirFull.EndsWith(Path.DirectorySeparatorChar)
                    ? dirFull : dirFull + Path.DirectorySeparatorChar;
                string tmpFull = Path.GetFullPath(tmpDir);
                string tmpPrefix = tmpFull.EndsWith(Path.DirectorySeparatorChar)
                    ? tmpFull : tmpFull + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    // Zip-slip guard: an entry named "..\evil.dll" must not escape the folder
                    // (compare with trailing separator so "dir-evil" cannot pass).
                    var target = Path.GetFullPath(Path.Combine(tmpDir, entry.FullName));
                    if (!target.StartsWith(tmpPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.Name.Length == 0)       // directory entry
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    try { entry.ExtractToFile(target, overwrite: true); }
                    catch (IOException) when (File.Exists(target))
                    {
                        // Target locked (e.g. elevated engine still running from it):
                        // keep the old folder, don't leave a half-extracted mess.
                        try { Directory.Delete(tmpDir, recursive: true); } catch { }
                        if (File.Exists(exe)) { _cachedExe = exe; return exe; }
                        throw;
                    }
                }
            }

            var tmpExe = Path.Combine(tmpDir, "OpenRGB.exe");
            if (!File.Exists(tmpExe))
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
                throw new FileNotFoundException($"engine bundle unpacked but OpenRGB.exe is missing in {tmpDir}");
            }

            if (File.Exists(marker) && File.Exists(exe))
            {
                // Another instance finished first while we extracted: prefer the complete one.
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
                _cachedExe = exe;
                return exe;
            }
            try
            {
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
                Directory.Move(tmpDir, dir);
            }
            catch
            {
                // Move lost the race (or dir locked): fall back to whichever is complete.
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
                if (!File.Exists(exe)) throw;
            }

            if (!File.Exists(exe))
                throw new FileNotFoundException($"engine bundle unpacked but OpenRGB.exe is missing in {dir}");

            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            _cachedExe = exe;
            return exe;
    }

    /// <summary>
    /// Deletes unpacked bundles other than the one in use. Called opportunistically so an engine
    /// upgrade does not leave 32 MB of dead files behind forever.
    ///
    /// The folder the elevated engine task points at is NEVER pruned: deleting it would leave a
    /// registered task aimed at nothing, and the user would have to re-approve UAC to fix a mess
    /// we created. Keeping a stale copy is the cheaper mistake.
    /// </summary>
    public static void PruneOldVersions(string keepExePath)
    {
        try
        {
            if (!Directory.Exists(RootDir)) return;

            var protect = new List<string> { Path.GetFullPath(Path.GetDirectoryName(keepExePath) ?? "") };
            var taskExe = Setup.EngineTask.RegisteredExePath();
            if (!string.IsNullOrEmpty(taskExe))
            {
                var taskDir = Path.GetDirectoryName(taskExe);
                if (!string.IsNullOrEmpty(taskDir)) protect.Add(Path.GetFullPath(taskDir));
            }

            foreach (var dir in Directory.GetDirectories(RootDir))
            {
                var full = Path.GetFullPath(dir);
                if (protect.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* engine may be running */ }
            }
        }
        catch { }
    }
}

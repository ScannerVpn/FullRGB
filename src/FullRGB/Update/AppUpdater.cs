using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullRGB.Update;

/// <summary>What the latest GitHub Release offers, parsed from the API.</summary>
public sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("asset_url")] public string AssetUrl { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
}

/// <summary>A download that waits to be swapped in on the next start.</summary>
public sealed class PendingUpdate
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("downloaded_at")] public string DownloadedAt { get; set; } = "";
}

/// <summary>
/// Auto-update for a single-file exe, end to end:
///   1. CHECK — GitHub Releases API (repos/ScannerVpn/FullRGB/releases/latest), 24 h cadence,
///      only when AutoUpdateEnabled; the tag must look newer than the running version.
///   2. DOWNLOAD — the FullRGB.exe asset goes to %LOCALAPPDATA%\FullRGB\update\, then the file's
///      SHA-256 and version are stored in pending.json (the swap is atomic, so the hash must be
///      fixed BEFORE the swap decision, not after).
///   3. APPLY — at the NEXT start (ApplyPendingOnStartup runs before anything touches the engine):
///      rename the running exe to FullRGB.exe.old (a running exe CAN be renamed on Windows),
///      move the downloaded file into its place, relaunch, exit. The .old copy is NOT deleted
///      immediately: a marker records how many crash-free starts the new build has had, and the
///      rollback copy survives until that count reaches <see cref="RequiredHealthyStarts"/>.
///      Stale downloads are pruned every start.
///
/// Security posture: HTTPS only, the owner/repo is a compile-time constant (an attacker cannot
/// repoint the check without rebuilding), the asset must be named FullRGB.exe, must start with
/// the MZ PE header and must pass its recorded SHA-256 before being swapped in.
///
/// Known limit: that SHA-256 is SELF-referential — it is compared against the hash recorded when
/// the file was downloaded, so it detects corruption and tampering between download and apply,
/// but it cannot prove the release itself came from us. A compromised GitHub account could
/// publish a malicious asset and this check would happily accept it. Real authenticity needs
/// Authenticode code-signing of the exe (see README → Security).
/// </summary>
public static class AppUpdater
{
    public const string Repo = "ScannerVpn/FullRGB";
    private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>Release LIST for the beta channel: GitHub never labels a pre-release "latest".</summary>
    private const string ReleasesUrl = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";

    /// <summary>Crash-free starts a freshly swapped build must reach before its rollback copy
    /// (FullRGB.exe.old) is allowed to be deleted.</summary>
    public const int RequiredHealthyStarts = 2;

    public static string UpdateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "FullRGB", "update");
    private static string PendingPath => Path.Combine(UpdateDir, "pending.json");

    /// <summary>Records the rollback copy left by the last swap and how many starts it survived.</summary>
    private sealed class UpdateHealth
    {
        [JsonPropertyName("old")] public string Old { get; set; } = "";
        [JsonPropertyName("starts")] public int Starts { get; set; }
    }

    private static string HealthMarkerPath => Path.Combine(UpdateDir, "rollback.json");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static string CurrentVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ------------------------------------------------------------------ check

    /// <summary>Latest release, or null (offline / no release / no usable asset). Never throws.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            // The beta channel asks for the release LIST instead of "latest": GitHub never calls a
            // pre-release "latest", so listing is the only way to opt into one. This is the safety
            // valve for shipping a risky change — round 21's HID code reached every user at once
            // because there was no earlier ring to catch it.
            bool beta = App.Settings.UpdateBetaChannel;
            using var req = new HttpRequestMessage(HttpMethod.Get, beta ? ReleasesUrl : ApiUrl);
            req.Headers.UserAgent.ParseAdd("FullRGB-update-check");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                UpdateInfo? newest = null;
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean()) continue;
                    var candidate = FromRelease(rel);
                    if (candidate is null) continue;
                    if (newest is null || IsNewer(newest.Version, candidate.Version)) newest = candidate;
                }
                return newest;
            }
            return FromRelease(doc.RootElement);
        }
        catch (Exception e)
        {
            Diag.AppLog.Warn("update check failed: " + e.Message);
            return null;
        }
    }

    /// <summary>One release object → UpdateInfo, or null when it is not newer or has no usable asset.</summary>
    private static UpdateInfo? FromRelease(JsonElement root)
    {
        string tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        string version = tag.TrimStart('v', 'V');
        if (!IsNewer(CurrentVersion(), version)) return null;

        string? assetUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Equals("FullRGB.exe", StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                break;
            }
        }
        // A release without the exe asset means "no update offered": the legacy "source exe attached
        // to the release body" layout is not supported.
        if (string.IsNullOrEmpty(assetUrl)) return null;

        string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        string html = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? "" : "";
        string published = root.TryGetProperty("published_at", out var pubEl) ? pubEl.GetString() ?? "" : "";
        return new UpdateInfo
        {
            Version = version,
            Notes = notes.Length > 4000 ? notes[..4000] + "…" : notes,
            AssetUrl = assetUrl,
            HtmlUrl = html,
            PublishedAt = published,
        };
    }

    /// <summary>Numeric compare "1.6.0" vs "1.5.1"; any non-numeric suffix (rc1, beta) is ignored.
    /// Equality counts as "not newer" so a re-published tag does not loop.</summary>
    public static bool IsNewer(string current, string latest)
    {
        try
        {
            var c = current.Split('.');
            var l = latest.Split('.');
            for (int i = 0; i < 3; i++)
            {
                int ci = i < c.Length && int.TryParse(c[i], out var cc) ? cc : 0;
                int li = i < l.Length && int.TryParse(l[i], out var ll) ? ll : 0;
                if (li != ci) return li > ci;
            }
            return false;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ download

    public sealed record DownloadProgress(long Received, long Total);

    /// <summary>Downloads the release exe and records the pending swap. Throws on failure.</summary>
    public static async Task<PendingUpdate> DownloadAsync(UpdateInfo info, Action<DownloadProgress>? progress = null)
    {
        Directory.CreateDirectory(UpdateDir);
        string target = Path.Combine(UpdateDir, $"FullRGB-{info.Version}.exe");

        using var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using (var outFs = new FileStream(target + ".part", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] buf = new byte[81920];
            long received = 0;
            int n;
            while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                await outFs.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                received += n;
                progress?.Invoke(new DownloadProgress(received, total));
            }
        }

        // Verify BEFORE it can ever be swapped in: size floor, MZ header, checksum.
        var fi = new FileInfo(target + ".part");
        if (fi.Length < 5 * 1024 * 1024)
            throw new IOException($"downloaded file too small ({fi.Length} bytes)");
        using (var fs = fi.OpenRead())
        {
            // byte[], not stackalloc: Span/stackalloc inside an async method needs a preview language feature.
            byte[] two = new byte[2];
            if (fs.Read(two, 0, 2) != 2 || two[0] != (byte)'M' || two[1] != (byte)'Z')
                throw new IOException("downloaded file is not a Windows executable");
        }
        string sha;
        using (var fs = fi.OpenRead())
            sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        File.Move(target + ".part", target, true);

        var pending = new PendingUpdate
        {
            Version = info.Version,
            File = target,
            Sha256 = sha,
            DownloadedAt = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending,
            new JsonSerializerOptions { WriteIndented = true }));
        Diag.AppLog.Info($"update {info.Version} downloaded and verified ({fi.Length} bytes)");
        return pending;
    }

    // ------------------------------------------------------------------ apply / cleanup

    /// <summary>
    /// Rolls back a freshly swapped build that never reached a single clean start.
    ///
    /// The signature: a rollback marker exists, it has recorded ZERO clean starts, the .old copy is
    /// still on disk, and the previous run did not exit cleanly. Together those mean the build we
    /// swapped in cannot start — which is exactly the case that used to leave the user with a
    /// bricked install and only a manual file rename as a way out.
    ///
    /// Must run BEFORE <see cref="CleanupStale"/> (which would delete the rollback copy) and before
    /// <see cref="SessionMarker.Begin"/> (which overwrites the previous run's state).
    /// </summary>
    public static bool TryRollbackBrokenUpdate(out string note)
    {
        note = "";
        try
        {
            var health = ReadHealthMarker();
            if (health is null || health.Starts > 0) return false;   // no swap, or it already started cleanly
            if (!File.Exists(health.Old)) return false;
            if (!Diag.SessionMarker.WasPreviousRunUnclean()) return false;

            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !File.Exists(exe)) return false;

            // Keep the broken build: it is the only artefact that can explain what went wrong, and
            // the user may want to attach it to a bug report.
            string broken = exe + ".failed";
            TryDelete(broken);
            File.Move(exe, broken);
            File.Move(health.Old, exe, true);
            TryDelete(HealthMarkerPath);
            note = $"the update never started cleanly — rolled back (broken build kept at {Path.GetFileName(broken)})";
            Diag.AppLog.Warn("update rollback: " + note);
            return true;
        }
        catch (Exception e)
        {
            Diag.AppLog.Warn("update rollback failed: " + e.Message);
            return false;
        }
    }

    /// <summary>Is a verified update waiting to be swapped in?</summary>
    public static PendingUpdate? ReadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return null;
            var p = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath));
            if (p is null || p.File.Length == 0 || !File.Exists(p.File)) { TryDelete(PendingPath); return null; }
            return p;
        }
        catch { return null; }
    }

    /// <summary>
    /// Called EARLY in OnStartup, before the engine or the UI exist. Swaps in a pending update
    /// (self-replace + relaunch, this instance exits) and prunes leftovers. Returns the args the
    /// caller should relaunch with, or null when there is nothing to do / no swap was possible.
    /// </summary>
    public static string[]? ApplyPendingOnStartup(string[] args)
    {
        CleanupStale();
        var pending = ReadPending();
        if (pending is null) return null;

        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0 || !File.Exists(exe)) return null;
        // Already the new version? (same file content) — just clear the flag.
        try
        {
            if (SameFile(exe, pending.File)) { TryDelete(PendingPath); TryDelete(pending.File); return null; }
        }
        catch { }

        try
        {
            using var fs = File.OpenRead(pending.File);
            // hash re-check at apply time
            string sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (sha != pending.Sha256)
            {
                Diag.AppLog.Warn($"pending update checksum mismatch — dropped ({sha} != {pending.Sha256})");
                TryDelete(PendingPath);
                TryDelete(pending.File);
                return null;
            }
        }
        catch (Exception e)
        {
            Diag.AppLog.Warn("pending update unreadable: " + e.Message);
            return null;
        }

        try
        {
            string old = exe + ".old";
            TryDelete(old);
            File.Move(exe, old);                 // legal while this process runs it
            File.Move(pending.File, exe, true);  // the new bytes take the old path
            TryDelete(PendingPath);
            // Keep the rollback copy for now — the new build has not proven it can start yet.
            // CleanupStale honours this marker and NoteHealthyStart() retires it.
            WriteHealthMarker(new UpdateHealth { Old = old, Starts = 0 });
            Diag.AppLog.Info($"update {pending.Version} applied — relaunching (rollback kept at {old})");
            return args;
        }
        catch (Exception e)
        {
            // A locked exe (some AV products hold it) must not brick the app: just log.
            Diag.AppLog.Warn("update swap failed: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// Deletes FullRGB.exe.old next to the current exe — but ONLY once the build that replaced
    /// it has started cleanly enough times. Removing it on the very next start would leave a
    /// build that crashes on launch with no way back.
    /// </summary>
    public static void CleanupStale()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var health = ReadHealthMarker();
                if (health is null || health.Starts >= RequiredHealthyStarts)
                {
                    TryDelete(exe + ".old");
                    if (health is not null) TryDelete(HealthMarkerPath);
                }
                // else: the swap has not proven itself yet — keep .old for rollback.
            }
        }
        catch { }
        try
        {
            if (Directory.Exists(UpdateDir))
                foreach (var f in new DirectoryInfo(UpdateDir).GetFiles())
                    // part files and downloads older than 7 days are junk
                    if (f.Extension == ".part" || f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                        TryDelete(f.FullName);
        }
        catch { }
    }

    /// <summary>
    /// Counts one start that got as far as a live engine session. Called from the startup path
    /// once the session is healthy: a build that crashes before that point never reaches here,
    /// which is exactly what keeps its rollback copy alive.
    /// </summary>
    public static void NoteHealthyStart()
    {
        try
        {
            var health = ReadHealthMarker();
            if (health is null) return;                  // no swap to judge
            health.Starts++;
            if (health.Starts >= RequiredHealthyStarts)
            {
                TryDelete(health.Old);
                TryDelete(HealthMarkerPath);
                Diag.AppLog.Info($"update rollback copy retired after {health.Starts} clean starts");
                return;
            }
            WriteHealthMarker(health);
            Diag.AppLog.Info($"clean start {health.Starts}/{RequiredHealthyStarts} — rollback copy kept");
        }
        catch { }
    }

    private static UpdateHealth? ReadHealthMarker()
    {
        try
        {
            if (!File.Exists(HealthMarkerPath)) return null;
            var h = JsonSerializer.Deserialize<UpdateHealth>(File.ReadAllText(HealthMarkerPath));
            return h is null || h.Old.Length == 0 ? null : h;
        }
        catch { return null; }
    }

    private static void WriteHealthMarker(UpdateHealth h)
    {
        try
        {
            Directory.CreateDirectory(UpdateDir);
            File.WriteAllText(HealthMarkerPath,
                JsonSerializer.Serialize(h, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var ha = fa.OpenRead();
        using var hb = fb.OpenRead();
        Span<byte> ba = stackalloc byte[8192], bb = stackalloc byte[8192];
        int na, nb;
        while ((na = ha.Read(ba)) > 0)
        {
            nb = hb.Read(bb);
            if (na != nb || !ba[..na].SequenceEqual(bb[..nb])) return false;
        }
        return hb.Read(bb) == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Relaunches with the given args (used after a successful swap).</summary>
    public static void Relaunch(string[] args)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}

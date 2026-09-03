using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace FullRGB.Setup;

public enum DependencyState { Missing, Installed, Installing, Failed }

/// <summary>One external dependency FullRGB can fetch and install on its own.</summary>
public sealed class Dependency
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Why { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string FileName { get; init; } = "";
    public string[] SilentArgs { get; init; } = Array.Empty<string>();
    public Func<bool> IsInstalled { get; init; } = () => false;
    public bool RequiresReboot { get; init; }
}

public sealed class DependencyProgress
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Stage { get; set; } = "";      // localized text supplied by caller
    public double Percent { get; set; }           // 0..100, -1 = indeterminate
    public long BytesReceived { get; set; }
    public long BytesTotal { get; set; }
    public DependencyState State { get; set; } = DependencyState.Missing;
    public string Error { get; set; } = "";
}

/// <summary>
/// Detects and installs the drivers FullRGB needs (currently PawnIO, which unlocks
/// SMBus/I2C access for RGB RAM, GPUs and some onboard lighting).
/// Downloads report progress so the UI can show a real progress bar.
/// </summary>
public static class DependencyManager
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullRGB", "downloads");

    public static IReadOnlyList<Dependency> All => new[]
    {
        new Dependency
        {
            Id = "pawnio",
            Name = "PawnIO driver",
            Why = "RGB RAM, graphics cards and some onboard lighting (SMBus/I2C access)",
            // pinned release (2.2.0) with a checksum, so a changed/compromised release
            // cannot be silently installed with admin rights.
            Url = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe",
            Sha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032",
            FileName = "PawnIO_setup.exe",
            // Inno Setup style silent switches; the installer accepts /S too.
            SilentArgs = new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" },
            IsInstalled = IsPawnIoInstalled,
        },
    };

    /// <summary>True when the file's SHA-256 matches the expected hex digest.</summary>
    public static bool VerifySha256(string path, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;
        try
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsPawnIoInstalled()
    {
        // 1) driver service registered?
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (key is not null) return true;
        }
        catch { }
        // 2) files shipped by the installer
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "PawnIO.sys"),
        })
        {
            if (File.Exists(p)) return true;
        }
        return false;
    }

    public static List<Dependency> Missing() => All.Where(d => !d.IsInstalled()).ToList();

    /// <summary>
    /// Downloads (with progress) and silently installs one dependency.
    /// Progress callbacks are raised on the calling thread's continuation context.
    /// </summary>
    public static async Task<bool> InstallAsync(Dependency dep, DependencyProgress progress,
                                                Action<DependencyProgress> report, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var target = Path.Combine(CacheDir, dep.FileName);

            progress.State = DependencyState.Installing;
            progress.Stage = "download";
            progress.Percent = -1;
            report(progress);

            using (var resp = await Http.GetAsync(dep.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                progress.BytesTotal = total;

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(target);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                var lastReport = DateTime.UtcNow;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress.BytesReceived = received;
                    progress.Percent = total > 0 ? received * 100.0 / total : -1;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        report(progress);
                        lastReport = DateTime.UtcNow;
                    }
                }
                progress.Percent = 100;
                report(progress);
            }

            if (dep.Sha256.Length > 0 && !VerifySha256(target, dep.Sha256))
            {
                progress.State = DependencyState.Failed;
                progress.Stage = "checksum-mismatch";
                progress.Error = "downloaded file failed SHA-256 verification";
                report(progress);
                return false;
            }

            progress.Stage = "install";
            progress.Percent = -1;
            report(progress);

            var psi = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                Verb = SDK.Elevation.IsElevated ? "" : "runas",
            };
            foreach (var a in dep.SilentArgs) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("installer did not start");
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            // Installer exit codes vary; trust the detector instead.
            await Task.Delay(1500, ct).ConfigureAwait(false);
            bool ok = dep.IsInstalled();
            progress.State = ok ? DependencyState.Installed : DependencyState.Failed;
            progress.Stage = ok ? "done" : "verify-failed";
            progress.Percent = 100;
            if (!ok) progress.Error = $"installer exited with code {proc.ExitCode} but the driver was not detected";
            report(progress);
            return ok;
        }
        catch (OperationCanceledException)
        {
            progress.State = DependencyState.Failed;
            progress.Stage = "cancelled";
            report(progress);
            return false;
        }
        catch (Exception e)
        {
            progress.State = DependencyState.Failed;
            progress.Stage = "error";
            progress.Error = e.Message;
            report(progress);
            return false;
        }
    }
}

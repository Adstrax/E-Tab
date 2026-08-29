using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ETab.Helpers;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? Message = null,
    ReleaseInfo? Release = null);

public sealed record ReleaseInfo(
    string TagName,
    Version Version,
    string ZipName,
    string ZipUrl,
    long ZipSize);

/// <summary>
/// Checks GitHub Releases for a newer E-Tab and installs it in place.
/// Update packages are the flat zips produced by pack.ps1
/// (E-Tab-&lt;version&gt;-win64.zip containing E-Tab.exe + README.txt).
///
/// Installation works by copying the current exe to the updates folder and
/// relaunching it in a "self-update" mode: the copy waits for this instance
/// to exit, replaces the exe on disk, relaunches the app and exits. The
/// running binary is therefore never overwritten while it is executing.
/// </summary>
public static class UpdateManager
{
    private const string RepoApiUrl = "https://api.github.com/repos/Adstrax/E-Tab/releases/latest";
    private const string UpdatesSubDir = "updates";
    private const string UpdaterExeName = "E-Tab-updater.exe";
    private const string MarkerFileName = "last-update.txt";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int _checking;
    private static int _installing;
    private static ReleaseInfo? _pendingUpdate;

    public static event Action<UpdateCheckResult>? CheckCompleted;
    public static event Action<string>? InstallCompleted;

    public static Version CurrentVersion => typeof(UpdateManager).Assembly.GetName().Version ?? new Version(0, 0);

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = GetSystemProxy(),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("E-Tab-Updater");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>
    /// Reads the Windows system proxy fresh for every operation so updates
    /// work even when the app was started before the proxy was enabled.
    /// </summary>
    private static IWebProxy? GetSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is int enabled && enabled == 1)
            {
                if (key.GetValue("AutoConfigURL") is string pac && !string.IsNullOrWhiteSpace(pac))
                    return WebRequest.GetSystemWebProxy();

                if (key.GetValue("ProxyServer") is string server && !string.IsNullOrWhiteSpace(server))
                {
                    // Accepts both "host:port" and "http=host:port;https=host:port".
                    var httpsPart = server
                        .Split(';')
                        .Select(p => p.Trim())
                        .FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase));
                    var address = httpsPart != null ? httpsPart["https=".Length..] : server;
                    if (!string.IsNullOrWhiteSpace(address))
                        return new WebProxy(address) { BypassProxyOnLocal = true };
                }
            }
        }
        catch
        {
            // Fall through to a direct connection.
        }

        return null;
    }

    private static string UpdatesDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "E-Tab",
            UpdatesSubDir);

    private static string MarkerPath => Path.Combine(UpdatesDir, MarkerFileName);

    /// <summary>
    /// Checks GitHub for the latest release and raises CheckCompleted with
    /// the outcome.
    /// </summary>
    public static async Task CheckForUpdatesAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            ReleaseInfo? release = null;
            string? error = null;
            try
            {
                using var client = CreateHttpClient();
                using var response = await client.GetAsync(RepoApiUrl, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    error = $"HTTP {(int)response.StatusCode}";
                }
                else
                {
                    var payload = await response.Content.ReadFromJsonAsync<GitHubRelease>(JsonOptions)
                        .ConfigureAwait(false);
                    release = payload?.ToReleaseInfo(CurrentVersion);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (release != null)
            {
                _pendingUpdate = release;
                Log.Info($"Update available: {release.TagName} ({release.ZipUrl}).");
                CheckCompleted?.Invoke(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Release: release));
            }
            else if (error == null)
            {
                CheckCompleted?.Invoke(new UpdateCheckResult(UpdateCheckStatus.UpToDate));
            }
            else
            {
                Log.Warn($"Update check failed: {error}");
                CheckCompleted?.Invoke(new UpdateCheckResult(UpdateCheckStatus.Failed, error));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// Downloads the release zip, validates it, extracts the new binary and
    /// hands over to a self-updater copy of the current exe. On success the
    /// app shuts down so the updater can replace the binary.
    /// </summary>
    public static async Task<bool> InstallUpdateAsync(
        ReleaseInfo info,
        IProgress<(long Received, long Total)>? progress = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _installing, 1) != 0) return false;
        try
        {
            var appExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(appExe))
                throw new InvalidOperationException("Cannot determine the application path.");

            var appDir = Path.GetDirectoryName(appExe)!;
            var updatesDir = UpdatesDir;
            Directory.CreateDirectory(updatesDir);

            var zipPath = Path.Combine(updatesDir, info.ZipName);
            var stageDir = Path.Combine(updatesDir, $"stage-{info.TagName.TrimStart('v')}");

            Log.Info($"Downloading update {info.TagName} from {info.ZipUrl}.");
            status?.Invoke("download");
            using (var client = CreateHttpClient())
            using (var response = await client
                       .GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var file = File.Create(zipPath);
                await using var content = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var buffer = new byte[81_920];
                long received = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report((received, total));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var exeEntry = archive.GetEntry("E-Tab.exe")
                               ?? throw new InvalidDataException("Update package does not contain E-Tab.exe.");
                if (exeEntry.Length < 100_000)
                    throw new InvalidDataException("Update package looks invalid.");

                if (Directory.Exists(stageDir))
                    Directory.Delete(stageDir, recursive: true);
                Directory.CreateDirectory(stageDir);
                exeEntry.ExtractToFile(Path.Combine(stageDir, "E-Tab.exe"), overwrite: true);
                archive.GetEntry("README.txt")?.ExtractToFile(Path.Combine(stageDir, "README.txt"), overwrite: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            status?.Invoke("install");
            var updaterExe = Path.Combine(updatesDir, UpdaterExeName);
            File.Copy(appExe, updaterExe, overwrite: true);

            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                WorkingDirectory = appDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["ETAB_UPDATE_PID"] = Environment.ProcessId.ToString();
            psi.Environment["ETAB_UPDATE_APP_EXE"] = appExe;
            psi.Environment["ETAB_UPDATE_APP_DIR"] = appDir;
            psi.Environment["ETAB_UPDATE_NEW_EXE"] = Path.Combine(stageDir, "E-Tab.exe");
            psi.Environment["ETAB_UPDATE_README"] = Path.Combine(stageDir, "README.txt");
            psi.Environment["ETAB_UPDATE_ZIP"] = zipPath;
            psi.Environment["ETAB_UPDATE_STAGE"] = stageDir;
            psi.Environment["ETAB_UPDATE_VERSION"] = info.Version.ToString(3);

            using var updater = Process.Start(psi);
            Log.Info($"Self-updater launched (pid {updater?.Id}); shutting down for replacement.");

            // Graceful shutdown so the single-instance mutex, WinEvent hooks
            // and COM objects are released before the updater replaces the exe.
            if (Application.Current != null)
                _ = Application.Current.Dispatcher.BeginInvoke((Action)(() => Application.Current.Shutdown()));
            else
                Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                Log.Info("Update installation cancelled by the user.");
                return false;
            }

            Log.Error("Update installation failed.", ex);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    /// <summary>
    /// Returns true when this process was started by the self-updater. In
    /// that case the update replacement is performed and the process must
    /// exit without starting the tray app.
    /// </summary>
    public static bool TryRunSelfUpdateMode()
    {
        var pidText = Environment.GetEnvironmentVariable("ETAB_UPDATE_PID");
        if (string.IsNullOrEmpty(pidText)) return false;

        var appExe = Environment.GetEnvironmentVariable("ETAB_UPDATE_APP_EXE") ?? string.Empty;
        var appDir = Environment.GetEnvironmentVariable("ETAB_UPDATE_APP_DIR") ?? string.Empty;
        var newExe = Environment.GetEnvironmentVariable("ETAB_UPDATE_NEW_EXE") ?? string.Empty;
        var readme = Environment.GetEnvironmentVariable("ETAB_UPDATE_README") ?? string.Empty;
        var zipPath = Environment.GetEnvironmentVariable("ETAB_UPDATE_ZIP") ?? string.Empty;
        var stageDir = Environment.GetEnvironmentVariable("ETAB_UPDATE_STAGE") ?? string.Empty;
        var version = Environment.GetEnvironmentVariable("ETAB_UPDATE_VERSION") ?? string.Empty;

        RunSelfUpdate(pidText, appExe, appDir, newExe, readme, zipPath, stageDir, version);
        return true;
    }

    /// <summary>
    /// Called on every normal startup: announces a just-applied update and
    /// removes leftover updater/staging files.
    /// </summary>
    public static void FinishPostUpdateCleanup()
    {
        try
        {
            var updatesDir = UpdatesDir;
            if (!Directory.Exists(updatesDir)) return;

            if (File.Exists(MarkerPath))
            {
                var version = File.ReadAllText(MarkerPath).Trim();
                try
                {
                    File.Delete(MarkerPath);
                }
                catch
                {
                    // Best effort; a leftover marker is harmless.
                }

                if (!string.IsNullOrEmpty(version))
                {
                    Log.Info($"E-Tab updated to v{version}.");
                    InstallCompleted?.Invoke(version);
                }
            }

            foreach (var file in Directory.EnumerateFiles(updatesDir, "*.exe"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // The updater may still be running; retried next launch.
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(updatesDir, "stage-*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Same as above.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update cleanup failed: {ex.Message}");
        }
    }

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag.Trim(), @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success) return null;
        return new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
    }

    public static bool IsNewerVersion(Version candidate, Version current)
    {
        if (candidate.Major != current.Major) return candidate.Major > current.Major;
        if (candidate.Minor != current.Minor) return candidate.Minor > current.Minor;
        return candidate.Build > current.Build;
    }

    private static void RunSelfUpdate(
        string pidText,
        string appExe,
        string appDir,
        string newExe,
        string readme,
        string zipPath,
        string stageDir,
        string version)
    {
        try
        {
            Log.Info("Self-update mode: waiting for the main instance to exit.");
            if (int.TryParse(pidText, out var pid))
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    if (!parent.WaitForExit(60_000))
                        Log.Warn("Timed out waiting for the main instance; continuing anyway.");
                }
                catch (ArgumentException)
                {
                    // The main instance already exited.
                }
            }

            if (!string.IsNullOrEmpty(appExe) && !string.IsNullOrEmpty(newExe) && File.Exists(newExe))
            {
                CopyWithRetry(newExe, appExe);
                if (!string.IsNullOrEmpty(readme) && File.Exists(readme))
                {
                    try
                    {
                        File.Copy(readme, Path.Combine(appDir, "README.txt"), overwrite: true);
                    }
                    catch
                    {
                        // README is optional.
                    }
                }
            }
            else
            {
                Log.Warn("Self-update: update payload missing; keeping the old binary.");
            }

            try
            {
                if (!string.IsNullOrEmpty(stageDir) && Directory.Exists(stageDir))
                    Directory.Delete(stageDir, recursive: true);
            }
            catch
            {
                // Cleaned up by the fresh instance later.
            }

            try
            {
                if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath))
                    File.Delete(zipPath);
            }
            catch
            {
                // Same as above.
            }

            if (!string.IsNullOrEmpty(version))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                    File.WriteAllText(MarkerPath, version);
                }
                catch
                {
                    // Announcement is best effort.
                }
            }

            // Relaunch regardless of the copy outcome: if the copy failed the
            // old binary is still in place, so the app does not disappear.
            if (!string.IsNullOrEmpty(appExe) && File.Exists(appExe))
            {
                LaunchApp(appExe, appDir);
                Log.Info("Self-update complete; relaunched the app.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Self-update failed.", ex);
            if (!string.IsNullOrEmpty(appExe) && File.Exists(appExe))
            {
                try
                {
                    LaunchApp(appExe, appDir);
                }
                catch
                {
                    // Nothing left to try.
                }
            }
        }
    }

    /// <summary>
    /// Starts the app without inheriting the ETAB_UPDATE_* environment
    /// variables. Without this the fresh instance would think it is still in
    /// self-update mode and relaunch itself forever.
    /// </summary>
    private static void LaunchApp(string appExe, string appDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = appExe,
            WorkingDirectory = appDir,
            UseShellExecute = false,
        };
        foreach (var name in SelfUpdateEnvVars)
            psi.Environment.Remove(name);
        Process.Start(psi);
    }

    private static readonly string[] SelfUpdateEnvVars =
    {
        "ETAB_UPDATE_PID",
        "ETAB_UPDATE_APP_EXE",
        "ETAB_UPDATE_APP_DIR",
        "ETAB_UPDATE_NEW_EXE",
        "ETAB_UPDATE_README",
        "ETAB_UPDATE_ZIP",
        "ETAB_UPDATE_STAGE",
        "ETAB_UPDATE_VERSION",
    };

    private static void CopyWithRetry(string source, string destination)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                Log.Info($"Replaced {destination}.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 19)
            {
                Thread.Sleep(500);
            }
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }

        public ReleaseInfo? ToReleaseInfo(Version current)
        {
            if (string.IsNullOrWhiteSpace(TagName)) return null;
            var version = ParseVersion(TagName);
            if (version == null || !IsNewerVersion(version, current)) return null;

            var asset = Assets?.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Name) &&
                a.Name!.StartsWith("E-Tab-", StringComparison.OrdinalIgnoreCase) &&
                a.Name!.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset == null || string.IsNullOrEmpty(asset.BrowserDownloadUrl)) return null;

            return new ReleaseInfo(TagName, version, asset.Name!, asset.BrowserDownloadUrl, asset.Size);
        }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

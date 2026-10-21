using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ETab.Helpers;

namespace ETab.Managers;

internal sealed record UpdateInfo(
    Version Version,
    string DisplayName,
    string DownloadUrl,
    string AssetName,
    string? Sha256);

internal static class UpdateManager
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Adstrax/E-Tab/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        if (!TryParseVersion(tag, out var latestVersion))
            throw new InvalidOperationException("The latest GitHub release has no valid version tag.");

        if (NormalizeVersion(latestVersion) <= NormalizeVersion(CurrentVersion))
            return null;

        var expectedAssetName = $"E-Tab-{latestVersion.ToString(3)}-win64.zip";
        JsonElement? asset = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var candidate in assets.EnumerateArray())
            {
                var name = candidate.TryGetProperty("name", out var candidateNameElement)
                    ? candidateNameElement.GetString()
                    : null;
                if (!string.Equals(name, expectedAssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                asset = candidate;
                break;
            }
        }

        if (asset is null)
            throw new InvalidOperationException(
                $"Release {latestVersion} does not contain {expectedAssetName}.");

        var assetValue = asset.Value;
        var downloadUrl = assetValue.TryGetProperty("browser_download_url", out var urlElement)
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("The update asset has no download URL.");

        var displayName = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        string? sha256 = null;
        if (assetValue.TryGetProperty("digest", out var digestElement))
        {
            var digest = digestElement.GetString();
            if (!string.IsNullOrWhiteSpace(digest) &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = digest["sha256:".Length..];
            }
        }

        return new UpdateInfo(
            latestVersion,
            string.IsNullOrWhiteSpace(displayName) ? $"E-Tab {latestVersion}" : displayName,
            downloadUrl,
            expectedAssetName,
            sha256);
    }

    public static async Task<string> DownloadExecutableAsync(
        UpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var archiveBytes = await client.GetByteArrayAsync(update.DownloadUrl, cancellationToken);

        if (!string.IsNullOrWhiteSpace(update.Sha256))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(archiveBytes));
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update did not match GitHub's SHA-256 digest.");
        }

        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "E-Tab",
            "updates",
            update.Version.ToString(3));
        Directory.CreateDirectory(updateDirectory);

        var executablePath = Path.Combine(updateDirectory, "E-Tab.exe");
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        var executableEntry = archive.Entries.FirstOrDefault(entry =>
            string.Equals(Path.GetFileName(entry.FullName), "E-Tab.exe", StringComparison.OrdinalIgnoreCase));
        if (executableEntry is null)
            throw new InvalidDataException("The update package does not contain E-Tab.exe.");

        await using (var input = executableEntry.Open())
        await using (var output = new FileStream(
                         executablePath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        return executablePath;
    }

    /// <summary>
    /// Starts a copy of the current executable from the temp directory. The
    /// helper waits for this process to exit, swaps the executable and starts
    /// the new version. Running the helper from temp is required because Windows
    /// keeps the executable image locked while it is running.
    /// </summary>
    public static void StartUpdater(string downloadedExecutable)
    {
        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable))
            throw new InvalidOperationException("E-Tab could not determine its executable path.");

        var updaterDirectory = Path.Combine(Path.GetTempPath(), "E-Tab", "updater");
        Directory.CreateDirectory(updaterDirectory);
        var helperExecutable = Path.Combine(
            updaterDirectory,
            $"E-Tab-updater-{Environment.ProcessId}.exe");
        File.Copy(currentExecutable, helperExecutable, overwrite: true);

        var startInfo = new ProcessStartInfo(helperExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = updaterDirectory,
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(downloadedExecutable);
        startInfo.ArgumentList.Add(currentExecutable);

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("The update helper could not be started.");
    }

    public static void ApplyUpdate(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out var parentProcessId))
        {
            Log.Error("The update helper received invalid arguments.");
            return;
        }

        var downloadedExecutable = args[2];
        var targetExecutable = args[3];
        var installed = false;

        try
        {
            WaitForParentExit(parentProcessId);

            var backupPath = targetExecutable +
                             $".bak-{GetFileVersion(targetExecutable)}-before-update";
            if (!File.Exists(backupPath))
                File.Copy(targetExecutable, backupPath, overwrite: false);

            CopyWithRetries(downloadedExecutable, targetExecutable);
            installed = true;
            Log.Info($"Update installed successfully: {targetExecutable}");
        }
        catch (Exception ex)
        {
            Log.Error("Installing the update failed.", ex);
            MessageBox.Show(
                "E-Tab could not install the update. The existing version will be started again.\n\n" +
                ex.Message,
                "E-Tab update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            TryStart(targetExecutable);
            if (installed)
                TryDeleteDirectory(Path.GetDirectoryName(downloadedExecutable));
        }
    }

    public static void CleanupStaleUpdaterFiles()
    {
        try
        {
            var updaterDirectory = Path.Combine(Path.GetTempPath(), "E-Tab", "updater");
            if (!Directory.Exists(updaterDirectory)) return;

            foreach (var path in Directory.EnumerateFiles(updaterDirectory, "E-Tab-updater-*.exe"))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // The immediately preceding updater may still be exiting.
                }
            }
        }
        catch
        {
            // Temp-file cleanup must never prevent E-Tab from starting.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("E-Tab-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var prerelease = normalized.IndexOf('-');
        if (prerelease >= 0)
            normalized = normalized[..prerelease];

        return Version.TryParse(normalized, out version!);
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static void WaitForParentExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(30_000))
                throw new TimeoutException("E-Tab did not exit before the update timed out.");
        }
        catch (ArgumentException)
        {
            // The parent has already exited.
        }
    }

    private static void CopyWithRetries(string source, string target)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(250);
            }
        }

        throw lastError ?? new IOException("The update file could not be copied.");
    }

    private static string GetFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryStart(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable),
            });
        }
        catch (Exception ex)
        {
            Log.Error("E-Tab could not be restarted after the update.", ex);
        }
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // The update remains usable; stale downloaded files are harmless.
        }
    }
}

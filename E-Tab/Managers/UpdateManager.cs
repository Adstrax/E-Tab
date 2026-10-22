using System;
using System.Buffers;
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
    string Sha256);

internal static class UpdateManager
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Adstrax/E-Tab/releases/latest";
    private const long MaxUpdateArchiveBytes = 32 * 1024 * 1024;
    private const long MaxExecutableBytes = 64 * 1024 * 1024;

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static string DisplayVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? CurrentVersion.ToString(3);

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
        if (!TryValidateDownloadUrl(downloadUrl, out var validatedDownloadUrl))
            throw new InvalidOperationException("The update asset has no trusted GitHub download URL.");

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

        if (!IsValidSha256(sha256))
        {
            throw new InvalidDataException(
                $"Release {latestVersion} does not provide a valid SHA-256 digest. " +
                "The update was not offered because its integrity cannot be verified.");
        }

        return new UpdateInfo(
            latestVersion,
            string.IsNullOrWhiteSpace(displayName) ? $"E-Tab {latestVersion}" : displayName,
            validatedDownloadUrl,
            expectedAssetName,
            sha256!);
    }

    public static async Task<string> DownloadExecutableAsync(
        UpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "E-Tab",
            "updates",
            update.Version.ToString(3));
        Directory.CreateDirectory(updateDirectory);

        var archivePath = Path.Combine(updateDirectory, "update.zip.tmp");
        var executablePath = Path.Combine(updateDirectory, "E-Tab.exe");
        TryDeleteFile(archivePath);

        using var client = CreateClient();
        using var response = await client.GetAsync(
            update.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxUpdateArchiveBytes)
            throw new InvalidDataException("The update archive is larger than the allowed limit.");

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var archiveFile = new FileStream(
                             archivePath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81_920,
                             useAsync: true))
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(81_920);
                try
                {
                    long totalBytes = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        totalBytes += read;
                        if (totalBytes > MaxUpdateArchiveBytes)
                            throw new InvalidDataException("The update archive is larger than the allowed limit.");

                        hasher.AppendData(buffer, 0, read);
                        await archiveFile.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded update did not match GitHub's SHA-256 digest.");
            }

            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                useAsync: true);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            var executableEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "E-Tab.exe", StringComparison.OrdinalIgnoreCase));
            if (executableEntry is null)
                throw new InvalidDataException("The update package does not contain E-Tab.exe at its root.");
            if (executableEntry.Length <= 0 || executableEntry.Length > MaxExecutableBytes)
                throw new InvalidDataException("The executable in the update package has an invalid size.");

            await using (var input = executableEntry.Open())
            await using (var output = new FileStream(
                             executablePath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81_920,
                             useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81_920);
                try
                {
                    long extractedBytes = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        extractedBytes += read;
                        if (extractedBytes > MaxExecutableBytes)
                            throw new InvalidDataException("The executable in the update package is too large.");

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (!TryGetFileVersion(executablePath, out var executableVersion) ||
                NormalizeVersion(executableVersion) != NormalizeVersion(update.Version))
            {
                throw new InvalidDataException(
                    $"The update executable does not report version {update.Version}.");
            }

            return executablePath;
        }
        catch
        {
            TryDeleteFile(executablePath);
            throw;
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
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

        using var updaterProcess = Process.Start(startInfo);
        if (updaterProcess is null)
            throw new InvalidOperationException("The update helper could not be started.");
    }

    public static void ApplyUpdate(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out var parentProcessId))
        {
            Log.Error("The update helper received invalid arguments.");
            return;
        }

        var downloadedExecutable = Path.GetFullPath(args[2]);
        var targetExecutable = Path.GetFullPath(args[3]);
        var targetDirectory = Path.GetDirectoryName(targetExecutable)
            ?? throw new InvalidOperationException("The installed executable has no parent directory.");
        var stagedExecutable = Path.Combine(
            targetDirectory,
            $".E-Tab-update-{Guid.NewGuid():N}.tmp");
        var backupExecutable = Path.Combine(
            targetDirectory,
            $".E-Tab-backup-{Guid.NewGuid():N}.tmp");
        var installed = false;

        try
        {
            WaitForParentExit(parentProcessId);

            if (!File.Exists(targetExecutable))
                throw new FileNotFoundException("The installed E-Tab executable was not found.", targetExecutable);
            if (!File.Exists(downloadedExecutable))
                throw new FileNotFoundException("The downloaded E-Tab executable was not found.", downloadedExecutable);

            // Stage on the target volume so File.Replace can atomically swap
            // the executable. Until that call succeeds, the installed file is
            // never overwritten.
            CopyWithRetries(downloadedExecutable, stagedExecutable);
            File.Replace(
                stagedExecutable,
                targetExecutable,
                backupExecutable,
                ignoreMetadataErrors: true);

            installed = TryStart(targetExecutable);
            if (!installed)
                throw new IOException("The updated E-Tab executable could not be started.");

            Log.Info($"Update installed successfully: {targetExecutable}");
        }
        catch (Exception ex)
        {
            if (File.Exists(backupExecutable))
            {
                try
                {
                    File.Replace(
                        backupExecutable,
                        targetExecutable,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                    Log.Info("The previous E-Tab executable was restored.");
                }
                catch (Exception rollbackException)
                {
                    Log.Error("Restoring the previous E-Tab executable failed.", rollbackException);
                }
            }

            Log.Error("Installing the update failed.", ex);
            MessageBox.Show(
                "E-Tab could not install the update. The previous version will be " +
                "started again when possible.\n\n" +
                ex.Message,
                "E-Tab update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            TryDeleteFile(stagedExecutable);

            if (installed)
            {
                CleanupOldBackups(targetDirectory, backupExecutable);
                TryDeleteDirectory(Path.GetDirectoryName(downloadedExecutable));
            }
            else
            {
                TryStart(targetExecutable);
            }
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

    private static bool TryValidateDownloadUrl(string? value, out string downloadUrl)
    {
        downloadUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        downloadUrl = uri.AbsoluteUri;
        return true;
    }

    private static bool IsValidSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;

        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
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

    private static bool TryGetFileVersion(string path, out Version version)
    {
        version = new Version();
        try
        {
            return Version.TryParse(
                FileVersionInfo.GetVersionInfo(path).FileVersion,
                out version!);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStart(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable),
            });
            return process != null;
        }
        catch (Exception ex)
        {
            Log.Error("E-Tab could not be restarted after the update.", ex);
            return false;
        }
    }

    private static void CleanupOldBackups(string directory, string currentBackup)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, ".E-Tab-backup-*.tmp"))
            {
                if (string.Equals(path, currentBackup, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryDeleteFile(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Old update backups could not be cleaned up: " + ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Stale files are harmless and are retried on later launches.
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

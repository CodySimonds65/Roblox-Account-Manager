using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxAltClient.Services;

public sealed record UpdatePackage(Version Version, string Tag, string ExecutablePath);

public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/CodySimonds65/roblox-alt-launcher/releases/latest";
    private const string ExecutableAssetName = "RobloxAltClient.exe";
    private const string ChecksumAssetName = "SHA256SUMS.txt";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public async Task<UpdatePackage?> CheckAndDownloadAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(responseStream, cancellationToken: cancellationToken)
                      ?? throw new InvalidOperationException("GitHub returned an empty release response.");

        if (!TryParseReleaseVersion(release.TagName, out var releaseVersion) || releaseVersion <= CurrentVersion)
        {
            return null;
        }

        var executableAsset = FindAsset(release, ExecutableAssetName);
        var checksumAsset = FindAsset(release, ChecksumAssetName);
        var checksumText = await HttpClient.GetStringAsync(checksumAsset.DownloadUrl, cancellationToken);
        var expectedHash = ParseSha256(checksumText, ExecutableAssetName)
                           ?? throw new InvalidOperationException("The release checksum file is invalid.");

        var updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient",
            "Updates");
        CleanupOldDownloads(updatesRoot, release.TagName);
        var updateDirectory = Path.Combine(updatesRoot, release.TagName);
        Directory.CreateDirectory(updateDirectory);

        var executablePath = Path.Combine(updateDirectory, ExecutableAssetName);
        if (!File.Exists(executablePath) || !HashesMatch(executablePath, expectedHash))
        {
            var temporaryPath = executablePath + ".download";
            try
            {
                await DownloadFileAsync(executableAsset.DownloadUrl, temporaryPath, cancellationToken);
                if (!HashesMatch(temporaryPath, expectedHash))
                {
                    throw new InvalidOperationException("The downloaded update did not match the release checksum.");
                }

                File.Move(temporaryPath, executablePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        return new UpdatePackage(releaseVersion, release.TagName, executablePath);
    }

    public static void StartInstaller(UpdatePackage package)
    {
        var targetPath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The current executable path could not be determined.");

        var startInfo = new ProcessStartInfo
        {
            FileName = package.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("The update installer could not be started.");
    }

    public static bool IsApplyUpdateMode(string[] args) =>
        args.Length == 3 && string.Equals(args[0], "--apply-update", StringComparison.Ordinal);

    public static void ApplyUpdate(string[] args)
    {
        if (!IsApplyUpdateMode(args) || !int.TryParse(args[2], out var processId))
        {
            throw new InvalidOperationException("The update installer arguments are invalid.");
        }

        var targetPath = Path.GetFullPath(args[1]);
        if (!string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update target is not a Windows executable.");
        }

        try
        {
            using var runningClient = Process.GetProcessById(processId);
            runningClient.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // The old client already exited.
        }

        var sourcePath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The downloaded executable path could not be determined.");
        var backupPath = targetPath + ".previous";
        File.Copy(targetPath, backupPath, overwrite: true);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                lastError = null;
                break;
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException exception)
            {
                lastError = exception;
                Thread.Sleep(500);
            }
        }

        if (lastError is not null)
        {
            File.Delete(backupPath);
            throw new InvalidOperationException(
                "Windows could not replace the existing Roblox Alt Client executable. Move it to a writable folder and try again.",
                lastError);
        }

        var confirmationPath = Path.Combine(
            Path.GetTempPath(),
            $"RobloxAltClient-update-{Guid.NewGuid():N}.ok");
        Process? updatedClient = null;
        Exception? updateError = null;
        try
        {
            var startInfo = new ProcessStartInfo { FileName = targetPath, UseShellExecute = false };
            startInfo.ArgumentList.Add("--confirm-update");
            startInfo.ArgumentList.Add(confirmationPath);
            updatedClient = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("The updated client could not be started.");

            for (var attempt = 0; attempt < 60; attempt++)
            {
                if (File.Exists(confirmationPath))
                {
                    File.Delete(confirmationPath);
                    File.Delete(backupPath);
                    return;
                }

                if (updatedClient.HasExited)
                {
                    break;
                }

                Thread.Sleep(500);
            }

            updateError = new InvalidOperationException("The updated client did not confirm a successful start.");
        }
        catch (Exception exception)
        {
            updateError = exception;
        }
        finally
        {
            if (File.Exists(confirmationPath))
            {
                File.Delete(confirmationPath);
            }
        }

        if (updatedClient is not null && !updatedClient.HasExited)
        {
            updatedClient.Kill(entireProcessTree: true);
            updatedClient.WaitForExit(10_000);
        }

        File.Copy(backupPath, targetPath, overwrite: true);
        File.Delete(backupPath);
        Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
        throw new InvalidOperationException("The update did not start successfully, so the previous version was restored.", updateError);
    }

    public static void ConfirmUpdatedLaunch(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--confirm-update", StringComparison.Ordinal))
        {
            return;
        }

        var confirmationPath = Path.GetFullPath(args[1]);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var fileName = Path.GetFileName(confirmationPath);
        if (!confirmationPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !fileName.StartsWith("RobloxAltClient-update-", StringComparison.Ordinal) ||
            !fileName.EndsWith(".ok", StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(confirmationPath, "ready");
    }

    public static bool TryParseReleaseVersion(string tag, out Version version)
    {
        var normalized = tag.Trim().TrimStart('v', 'V');
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    public static string? ParseSha256(string checksumText, string assetName)
    {
        foreach (var line in checksumText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                parts[0].Length == 64 &&
                parts[0].All(Uri.IsHexDigit) &&
                string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    private static GitHubAsset FindAsset(GitHubRelease release, string name) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"The latest release does not contain {name}.");

    private static async Task DownloadFileAsync(Uri downloadUrl, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static bool HashesMatch(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actualHash),
            Convert.FromHexString(expectedHash));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAltClient-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void CleanupOldDownloads(string updatesRoot, string currentTag)
    {
        if (!Directory.Exists(updatesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
        {
            if (string.Equals(Path.GetFileName(directory), currentTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A prior updater may still be exiting. Cleanup can wait until the next check.
            }
            catch (UnauthorizedAccessException)
            {
                // Update checks should not fail because an old staging directory is locked.
            }
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] Uri DownloadUrl);
}

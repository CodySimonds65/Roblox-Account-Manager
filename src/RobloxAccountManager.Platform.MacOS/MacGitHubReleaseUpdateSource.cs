using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>
/// Retrieves one explicitly selected macOS release channel from GitHub. Release metadata and
/// package metadata are both treated as untrusted until the package installer validates them.
/// </summary>
public sealed class MacGitHubReleaseUpdateSource : IPlatformUpdateSource
{
    private const string Repository = "CodySimonds65/Roblox-Account-Manager";
    private const string PackageIdentifier = "io.github.codysimonds65.roblox-account-manager";
    private readonly HttpClient _httpClient;
    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly string _rid;
    private readonly string _stagingRoot;

    public MacGitHubReleaseUpdateSource(
        HttpClient? httpClient = null,
        IMacProcessCommandRunner? commandRunner = null,
        string? rid = null,
        string? stagingRoot = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RobloxAccountManager", "1.0"));
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _rid = rid ?? MacPkgUpdateInstaller.GetCurrentRid();
        _stagingRoot = Path.GetFullPath(stagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "RobloxAccountManager", "Updates", "verified"));
    }

    public RobloxPlatform Platform => RobloxPlatform.MacOS;

    public async ValueTask<UpdatePackage?> DownloadLatestAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (_rid is not ("osx-arm64" or "osx-x64"))
            throw new InvalidOperationException("Unsupported macOS architecture.");

        using var releasesResponse = await _httpClient.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases?per_page=30",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!releasesResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub release lookup failed ({(int)releasesResponse.StatusCode}).");

        await using var releaseStream = await releasesResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var releases = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) continue;
            if (!release.TryGetProperty("tag_name", out var tag) || !TryParseReleaseVersion(tag.GetString(), out var version)) continue;
            if (!release.TryGetProperty("assets", out var assets)) continue;

            var packageName = $"RobloxAccountManager-{version}-{_rid}{(channel == UpdateChannel.Unsigned ? "-unsigned" : string.Empty)}.pkg";
            var checksumName = packageName + ".sha256";
            var packageAsset = FindAsset(assets, packageName);
            var checksumAsset = FindAsset(assets, checksumName);
            if (packageAsset is null || checksumAsset is null) continue;

            var packageUri = GetAssetUri(packageAsset.Value);
            var checksumUri = GetAssetUri(checksumAsset.Value);
            var expectedHash = await ReadChecksumAsync(checksumUri, packageName, cancellationToken).ConfigureAwait(false);
            var stagedPath = await DownloadAndVerifyAsync(packageUri, expectedHash, packageName, version, channel, cancellationToken).ConfigureAwait(false);
            var packageVersion = await ReadPackageInfoVersionAsync(stagedPath, cancellationToken).ConfigureAwait(false);
            if (packageVersion is null)
            {
                TryDelete(stagedPath);
                throw new InvalidDataException("The update PKG did not contain the expected PackageInfo version.");
            }

            return new UpdatePackage(
                RobloxPlatform.MacOS,
                _rid,
                version,
                packageVersion,
                packageUri,
                expectedHash,
                stagedPath,
                channel == UpdateChannel.Unsigned);
        }

        return null;
    }

    private async Task<string> DownloadAndVerifyAsync(
        Uri packageUri,
        string expectedHash,
        string packageName,
        Version version,
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        PathSafety.EnsureOwnerOnlyDirectory(_stagingRoot);
        var suffix = channel == UpdateChannel.Unsigned ? "-unsigned" : string.Empty;
        var stagedPath = PathSafety.RequireContainedPath(_stagingRoot,
            Path.Combine(_stagingRoot, $"update-{version}-{_rid}{suffix}.pkg"));
        var temporaryPath = PathSafety.RequireContainedPath(_stagingRoot,
            Path.Combine(_stagingRoot, $"download-{Guid.NewGuid():N}.tmp"));
        try
        {
            using var response = await _httpClient.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            PathSafety.RejectSymlinkComponents(temporaryPath);
            PathSafety.RejectSymlink(temporaryPath);
            await using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
            {
                var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash)))
                    throw new InvalidDataException($"Checksum validation failed for {packageName}.");
            }

            PathSafety.RejectSymlinkComponents(stagedPath);
            if (File.Exists(stagedPath))
            {
                PathSafety.RejectSymlink(stagedPath);
                File.Delete(stagedPath);
            }
            File.Move(temporaryPath, stagedPath);
            PathSafety.RejectSymlinkComponents(stagedPath);
            PathSafety.RejectSymlink(stagedPath);
            return stagedPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task<string?> ReadPackageInfoVersionAsync(string packagePath, CancellationToken cancellationToken)
    {
        var expansionParent = PathSafety.RequireContainedPath(_stagingRoot, Path.Combine(_stagingRoot, $"inspect-{Guid.NewGuid():N}"));
        var expansionRoot = PathSafety.RequireContainedPath(expansionParent, Path.Combine(expansionParent, "expanded"));
        try
        {
            PathSafety.EnsureOwnerOnlyDirectory(expansionParent);
            var expanded = await _commandRunner.RunAsync(
                "/usr/sbin/pkgutil",
                ["--expand-full", "--", packagePath, expansionRoot],
                cancellationToken).ConfigureAwait(false);
            if (!expanded.Succeeded) return null;

            RejectUnsafeExpandedEntries(expansionRoot);

            var packageInfos = Directory.EnumerateFiles(expansionRoot, "PackageInfo", SearchOption.AllDirectories).ToArray();
            if (packageInfos.Length != 1) return null;
            var root = XDocument.Parse(await File.ReadAllTextAsync(packageInfos[0], cancellationToken).ConfigureAwait(false)).Root;
            if (root is null || !string.Equals(root.Attribute("identifier")?.Value, PackageIdentifier, StringComparison.Ordinal)) return null;
            var version = root.Attribute("version")?.Value;
            return ulong.TryParse(version, out var numeric) && numeric > 0 ? version : null;
        }
        finally
        {
            if (Directory.Exists(expansionParent) && PathSafety.IsContainedBy(_stagingRoot, expansionParent))
            {
                try { Directory.Delete(expansionParent, recursive: true); } catch { }
            }
        }
    }

    private async Task<string> ReadChecksumAsync(Uri checksumUri, string packageName, CancellationToken cancellationToken)
    {
        var text = await _httpClient.GetStringAsync(checksumUri, cancellationToken).ConfigureAwait(false);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0].Length != 64 || !tokens[0].All(Uri.IsHexDigit))
            throw new InvalidDataException($"The checksum asset for {packageName} is invalid.");
        if (tokens.Length > 1 && !string.Equals(Path.GetFileName(tokens[^1]), packageName, StringComparison.Ordinal))
            throw new InvalidDataException($"The checksum asset does not name {packageName}.");
        return tokens[0].ToLowerInvariant();
    }

    private static JsonElement? FindAsset(JsonElement assets, string name)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var assetName) && string.Equals(assetName.GetString(), name, StringComparison.Ordinal))
                return asset;
        }
        return null;
    }

    private static Uri GetAssetUri(JsonElement asset)
    {
        if (!asset.TryGetProperty("browser_download_url", out var value) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The GitHub asset URL is not an HTTPS URI.");
        return uri;
    }

    private static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        version = new Version();
        var value = tag?.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(value, out var parsed) || parsed is null) return false;
        version = parsed;
        return true;
    }

    private static void RejectUnsafeExpandedEntries(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            PathSafety.RejectSymlinkDirectory(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                PathSafety.RejectSymlink(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

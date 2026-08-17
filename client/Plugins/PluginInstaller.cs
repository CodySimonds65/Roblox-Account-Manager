using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace RobloxAltClient.Plugins;

public sealed class PluginInstaller
{
    private readonly PluginPaths _paths;
    private readonly PluginConsentStore _consent;
    private readonly HttpClient _http;
    private readonly Func<string, Task> _stopPluginAsync;
    private readonly IPluginPackageSignatureVerifier? _signatureVerifier;

    public PluginInstaller(
        PluginPaths paths,
        PluginConsentStore consent,
        HttpClient? http = null,
        Func<string, Task>? stopPluginAsync = null,
        IPluginPackageSignatureVerifier? signatureVerifier = null)
    {
        _paths = paths;
        _consent = consent;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager-PluginInstaller");
        _stopPluginAsync = stopPluginAsync ?? (_ => Task.CompletedTask);
        _signatureVerifier = signatureVerifier;
    }

    public async Task<InstalledPlugin> InstallFromUrlAsync(
        string baseUrl,
        bool requireTrustedSignature = true,
        bool allowUnsignedSideload = false,
        string? expectedPluginId = null,
        string? expectedPublisher = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Plugin install URLs must use https://.");
        }

        var normalized = baseUri.AbsoluteUri.EndsWith('/') ? baseUri.AbsoluteUri : baseUri.AbsoluteUri + "/";
        var manifestJson = await DownloadStringAsync(new Uri(normalized + "plugin.json"), cancellationToken);
        var manifest = PluginManifestReader.Parse(manifestJson);
        if (expectedPluginId is not null && !string.Equals(manifest.Id, expectedPluginId, StringComparison.Ordinal) ||
            expectedPublisher is not null && !string.Equals(manifest.Publisher, expectedPublisher, StringComparison.Ordinal))
            throw new InvalidDataException("The package manifest identity does not match its catalog entry.");
        var packageBytes = await DownloadBytesAsync(new Uri(normalized + "plugin.zip"), cancellationToken);
        if (packageBytes.Length == 0 || packageBytes.Length > 250 * 1024 * 1024)
        {
            throw new InvalidDataException("Plugin package size is outside the allowed range.");
        }

        var expectedHashText = await DownloadStringAsync(new Uri(normalized + "plugin.sha256"), cancellationToken);
        var expectedHash = ParseHash(expectedHashText);
        var actualHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
        {
            throw new InvalidDataException("Plugin package SHA-256 does not match plugin.sha256.");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = await DownloadBytesAsync(new Uri(normalized + "plugin.sig"), cancellationToken);
        }
        catch (HttpRequestException) when (!requireTrustedSignature && allowUnsignedSideload)
        {
            signatureBytes = [];
        }

        if (requireTrustedSignature && _signatureVerifier is null)
            throw new InvalidOperationException("Official plugin verification is not configured; installation was refused.");
        if (signatureBytes.Length == 0)
        {
            if (!allowUnsignedSideload || requireTrustedSignature)
                throw new InvalidDataException("The package is unsigned. Explicit sideload consent is required.");
        }
        else if (_signatureVerifier is null || !_signatureVerifier.Verify(packageBytes, signatureBytes))
        {
            throw new InvalidDataException("Plugin package signature verification failed.");
        }

        var installDirectory = _paths.GetInstallDirectory(manifest.Id);
        await _stopPluginAsync(manifest.Id);
        var stagingDirectory = installDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            ExtractSafely(packageBytes, stagingDirectory);
            var embeddedManifestPath = Path.Combine(stagingDirectory, "plugin.json");
            if (!File.Exists(embeddedManifestPath))
                throw new InvalidDataException("Plugin package is missing its embedded plugin.json.");
            var embeddedManifest = PluginManifestReader.Parse(await File.ReadAllTextAsync(embeddedManifestPath, cancellationToken));
            if (!ManifestsMatch(manifest, embeddedManifest))
                throw new InvalidDataException("The downloaded manifest does not match the manifest inside plugin.zip.");

            var entryPoint = Path.Combine(stagingDirectory, manifest.EntryPoint);
            if (!File.Exists(entryPoint))
            {
                throw new InvalidDataException($"Plugin entry point '{manifest.EntryPoint}' is missing.");
            }

            var backupDirectory = installDirectory + ".previous";
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            if (Directory.Exists(installDirectory)) Directory.Move(installDirectory, backupDirectory);
            try
            {
                Directory.Move(stagingDirectory, installDirectory);
            }
            catch
            {
                if (!Directory.Exists(installDirectory) && Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, installDirectory);
                throw;
            }
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }

        var existing = _consent.Get(manifest.Id, manifest.AutostartDefault);
        var effectiveCapabilities = existing.GrantedCapabilities.Intersect(manifest.Capabilities, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return new InstalledPlugin(
            manifest,
            installDirectory,
            existing.Autostart,
            effectiveCapabilities,
            false,
            null,
            null);
    }

    public async Task<bool> RollbackAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var installDirectory = _paths.GetInstallDirectory(pluginId);
        var backupDirectory = installDirectory + ".previous";
        if (!Directory.Exists(backupDirectory)) return false;
        var backupManifestPath = Path.Combine(backupDirectory, "plugin.json");
        if (!File.Exists(backupManifestPath)) throw new InvalidDataException("The rollback package has no manifest.");
        var backupManifest = PluginManifestReader.Parse(await File.ReadAllTextAsync(backupManifestPath, cancellationToken));
        if (!string.Equals(backupManifest.Id, pluginId, StringComparison.Ordinal))
            throw new InvalidDataException("The rollback package identity does not match the requested plugin.");
        var backupEntrypoint = Path.GetFullPath(Path.Combine(backupDirectory, backupManifest.EntryPoint));
        var backupRoot = Path.GetFullPath(backupDirectory) + Path.DirectorySeparatorChar;
        if (!backupEntrypoint.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(backupEntrypoint))
            throw new InvalidDataException("The rollback package entrypoint is invalid or missing.");
        await _stopPluginAsync(pluginId).ConfigureAwait(false);
        var failedDirectory = installDirectory + ".failed-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(installDirectory)) Directory.Move(installDirectory, failedDirectory);
            Directory.Move(backupDirectory, installDirectory);
            try { Directory.Delete(failedDirectory, recursive: true); } catch { }
        }
        catch
        {
            if (!Directory.Exists(installDirectory) && Directory.Exists(failedDirectory))
            {
                try { Directory.Move(failedDirectory, installDirectory); } catch { }
            }
            throw;
        }
        return true;
    }

    private static bool ManifestsMatch(PluginManifest expected, PluginManifest actual) =>
        expected.SchemaVersion == actual.SchemaVersion &&
        string.Equals(expected.Id, actual.Id, StringComparison.Ordinal) &&
        string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) &&
        string.Equals(expected.Version, actual.Version, StringComparison.Ordinal) &&
        string.Equals(expected.ContractVersion, actual.ContractVersion, StringComparison.Ordinal) &&
        string.Equals(expected.Publisher, actual.Publisher, StringComparison.Ordinal) &&
        string.Equals(expected.Description, actual.Description, StringComparison.Ordinal) &&
        expected.Capabilities.SequenceEqual(actual.Capabilities, StringComparer.Ordinal) &&
        string.Equals(expected.EntryPoint, actual.EntryPoint, StringComparison.Ordinal) &&
        string.Equals(expected.Icon, actual.Icon, StringComparison.Ordinal) &&
        string.Equals(expected.UpdateFeed, actual.UpdateFeed, StringComparison.Ordinal) &&
        string.Equals(expected.MinHostVersion, actual.MinHostVersion, StringComparison.Ordinal) &&
        expected.AutostartDefault == actual.AutostartDefault;

    public static string ParseHash(string text)
    {
        var hash = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.Length == 64 && value.All(Uri.IsHexDigit));
        return hash?.ToLowerInvariant() ?? throw new InvalidDataException("plugin.sha256 is invalid.");
    }

    private static void ExtractSafely(byte[] bytes, string root)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > 20_000) throw new InvalidDataException("Plugin package contains too many entries.");
        var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length > 260 || entry.FullName.Contains('\0'))
                throw new InvalidDataException("Plugin archive contains an invalid path.");
            var normalized = entry.FullName.Replace('\\', '/');
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidDataException("Plugin archive symlinks are not allowed.");
            if (normalized.Split('/').Any(part => part is "" or "." or "..") || Path.IsPathRooted(normalized) || normalized.Contains(':'))
                throw new InvalidDataException("Plugin archive contains a path traversal entry.");
            if (entry.Length > 100 * 1024 * 1024 || (totalBytes += entry.Length) > 500 * 1024 * 1024)
                throw new InvalidDataException("Plugin archive is too large after extraction.");

            var destination = Path.GetFullPath(Path.Combine(root, normalized));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin archive escapes its install directory.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private async Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        const long maxBytes = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maxBytes) throw new InvalidDataException("Plugin metadata is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidDataException("Plugin metadata is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private async Task<byte[]> DownloadBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        const long maxBytes = 250L * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maxBytes)
            throw new InvalidDataException("Plugin download exceeds the size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidDataException("Plugin download exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }
}

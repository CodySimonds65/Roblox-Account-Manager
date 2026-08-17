using System.IO.Compression;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RobloxAltClient.Plugins;

public sealed class PluginInstaller
{
    // Official plugins are published as self-contained single-file Windows
    // applications. Those binaries are larger than the old 100 MiB per-entry
    // guard even though the complete package remains modest. Keep the archive
    // and expanded-package caps independent so a larger legitimate executable
    // does not disable zip-bomb protection.
    internal const long MaxArchiveEntryBytes = 256L * 1024 * 1024;
    internal const long MaxArchiveExtractedBytes = 500L * 1024 * 1024;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

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
        string manifestJson;
        try
        {
            manifestJson = await DownloadStringAsync(new Uri(normalized + "plugin.json"), cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"No plugin release assets were found at '{normalized}'. Publish plugin.json, plugin.zip, plugin.sha256, and plugin.sig before installing.", ex);
        }
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
        EnsureNoReparsePointsInPath(_paths.InstallRoot);
        EnsureNoReparsePointsInPath(installDirectory);
        await _stopPluginAsync(manifest.Id);
        var stagingDirectory = installDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            EnsureNoReparsePointsInPath(stagingDirectory);
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

    internal static void ExtractSafely(byte[] bytes, string root)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
        var fullRoot = Path.GetFullPath(root);
        EnsureNoReparsePoints(fullRoot, fullRoot);
        ValidateArchiveEntries(archive, fullRoot);
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(fullRoot, normalized));
            // ValidateArchiveEntries has already checked this prefix. Keeping
            // the extraction loop focused on writing prevents checks from
            // drifting between the validation and extraction paths.
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                EnsureNoReparsePoints(fullRoot, destination);
                continue;
            }

            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            EnsureNoReparsePoints(fullRoot, parent);
            EnsureNoReparsePoints(fullRoot, destination);
            using var input = entry.Open();
            using var output = CreateNoFollowFile(destination);
            input.CopyTo(output);
            EnsureNoReparsePoints(fullRoot, destination);
        }
    }

    /// <summary>
    /// Validates archive metadata before any filesystem writes occur.
    /// </summary>
    internal static void ValidateArchiveEntries(ZipArchive archive, string root)
    {
        ValidateArchiveMetadata(
            archive.Entries.Select(entry =>
                (FullName: entry.FullName, Length: entry.Length, ExternalAttributes: entry.ExternalAttributes)),
            root);
    }

    /// <summary>
    /// Validates archive metadata without opening entry streams. The metadata
    /// overload keeps boundary and malicious-path checks directly testable.
    /// </summary>
    internal static void ValidateArchiveMetadata(
        IEnumerable<(string FullName, long Length, int ExternalAttributes)> entries,
        string root)
    {
        var metadata = entries.ToArray();
        if (metadata.Length > 20_000) throw new InvalidDataException("Plugin package contains too many entries.");
        var rootPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        long totalBytes = 0;
        foreach (var entry in metadata)
        {
            if (entry.FullName.Length > 260 || entry.FullName.Contains('\0'))
                throw new InvalidDataException("Plugin archive contains an invalid path.");
            var normalized = entry.FullName.Replace('\\', '/');
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidDataException("Plugin archive symlinks are not allowed.");
            if (normalized.Split('/').Any(part => part is "" or "." or "..") || Path.IsPathRooted(normalized) || normalized.Contains(':'))
                throw new InvalidDataException("Plugin archive contains a path traversal entry.");
            if (entry.Length > MaxArchiveEntryBytes || (totalBytes += entry.Length) > MaxArchiveExtractedBytes)
                throw new InvalidDataException("Plugin archive is too large after extraction.");

            var destination = Path.GetFullPath(Path.Combine(root, normalized));
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin archive escapes its install directory.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string target)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullTarget = Path.GetFullPath(target);
        var relative = Path.GetRelativePath(fullRoot, fullTarget);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Plugin archive escapes its install directory.");

        CheckReparsePoint(fullRoot);
        if (relative == ".") return;

        var current = fullRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CheckReparsePoint(current);
        }
    }

    private static void EnsureNoReparsePointsInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var filesystemRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("Plugin staging path has no filesystem root.");
        CheckReparsePoint(filesystemRoot);
        var relative = Path.GetRelativePath(filesystemRoot, fullPath);
        var current = filesystemRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            CheckReparsePoint(current);
        }
    }

    private static void CheckReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin staging paths cannot contain reparse points.");
        }
        catch (FileNotFoundException)
        {
            // A file destination is expected not to exist before extraction.
        }
        catch (DirectoryNotFoundException)
        {
            // A not-yet-created directory is checked again after creation.
        }
    }

    private static FileStream CreateNoFollowFile(string path)
    {
        var handle = CreateFile(
            path,
            GenericWrite,
            0,
            IntPtr.Zero,
            CreateNew,
            FileAttributeNormal | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not create plugin file without following reparse points: {new Win32Exception(error).Message}", error);
        }

        try
        {
            return new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

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

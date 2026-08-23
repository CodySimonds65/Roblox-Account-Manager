using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RobloxAccountManager.Platform.MacOS;

public sealed class MacBundleDiscovery
{
    public const string RobloxBundleIdentifier = "com.roblox.RobloxPlayer";
    public const string RobloxExecutableName = "RobloxPlayer";

    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly MacSignatureVerifier _signatureVerifier;
    private readonly IReadOnlyList<string> _approvedLocations;

    public MacBundleDiscovery(
        IMacProcessCommandRunner? commandRunner = null,
        MacSignatureVerifier? signatureVerifier = null)
        : this(commandRunner, signatureVerifier, approvedLocations: null)
    {
    }

    internal MacBundleDiscovery(
        IMacProcessCommandRunner? commandRunner,
        MacSignatureVerifier? signatureVerifier,
        IReadOnlyList<string>? approvedLocations)
    {
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _signatureVerifier = signatureVerifier ?? new MacSignatureVerifier(_commandRunner);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _approvedLocations = approvedLocations?.Select(Path.GetFullPath).ToArray()
            ?? ["/Applications", Path.Combine(home, "Applications")];
    }

    public IReadOnlyList<string> GetDefaultBundleCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            "/Applications/Roblox.app",
            Path.Combine(home, "Applications", "Roblox.app")
        ];
    }

    public async Task<MacBundleInfo?> DiscoverAsync(
        string? selectedPath = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = string.IsNullOrWhiteSpace(selectedPath)
            ? GetDefaultBundleCandidates()
            : [selectedPath];
        foreach (var candidate in candidates)
        {
            var discovered = await ValidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (discovered is not null)
            {
                return discovered;
            }
        }

        return null;
    }

    public async Task<MacBundleInfo?> ValidateAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        return await ValidateCoreAsync(bundlePath, requireApprovedLocation: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MacBundleInfo?> ValidateManagedRuntimeAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        return await ValidateCoreAsync(bundlePath, requireApprovedLocation: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MacBundleInfo?> ValidateManagedMultiInstanceRuntimeAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        var validated = await ValidateCoreAsync(
            bundlePath,
            requireApprovedLocation: false,
            cancellationToken).ConfigureAwait(false);
        if (validated is null)
            return null;

        var plistPath = Path.Combine(validated.BundlePath, "Contents", "Info.plist");
        var prohibited = await ReadPlistValueAsync(
            plistPath,
            "LSMultipleInstancesProhibited",
            cancellationToken).ConfigureAwait(false);
        return string.Equals(prohibited, "false", StringComparison.OrdinalIgnoreCase)
            ? validated
            : null;
    }

    private async Task<MacBundleInfo?> ValidateCoreAsync(
        string bundlePath,
        bool requireApprovedLocation,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(bundlePath);
            if (!fullPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(fullPath)
                || requireApprovedLocation && !IsApprovedLocation(fullPath))
            {
                return null;
            }

            PathSafety.RejectSymlinkDirectory(fullPath);
            var contents = PathSafety.RequireContainedPath(fullPath, Path.Combine(fullPath, "Contents"));
            var macOs = PathSafety.RequireContainedPath(fullPath, Path.Combine(contents, "MacOS"));
            var plistPath = PathSafety.RequireContainedPath(fullPath, Path.Combine(contents, "Info.plist"));
            if (!File.Exists(plistPath))
            {
                return null;
            }

            var identifier = await ReadPlistValueAsync(plistPath, "CFBundleIdentifier", cancellationToken).ConfigureAwait(false);
            var executableName = await ReadPlistValueAsync(plistPath, "CFBundleExecutable", cancellationToken).ConfigureAwait(false);

            // Some current Roblox macOS bundles omit these root-plist keys even though the
            // signed bundle still identifies itself as com.roblox.RobloxPlayer and ships the
            // canonical RobloxPlayer executable. The code-signature validation below remains
            // authoritative on macOS; these fallbacks only keep discovery from rejecting that
            // valid bundle before launch.
            identifier = string.IsNullOrWhiteSpace(identifier)
                ? RobloxBundleIdentifier
                : identifier;
            executableName = string.IsNullOrWhiteSpace(executableName)
                ? RobloxExecutableName
                : executableName;
            if (!string.Equals(identifier, RobloxBundleIdentifier, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(executableName)
                || executableName.Contains(Path.DirectorySeparatorChar)
                || executableName.Contains(Path.AltDirectorySeparatorChar))
            {
                return null;
            }

            var executablePath = PathSafety.RequireContainedPath(fullPath, Path.Combine(macOs, executableName));
            if (!File.Exists(executablePath))
            {
                return null;
            }

            PathSafety.RejectSymlink(executablePath);
            var signatureVerified = requireApprovedLocation
                ? await _signatureVerifier.VerifyAsync(fullPath, cancellationToken).ConfigureAwait(false)
                : await _signatureVerifier.VerifyManagedAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsMacOS() && !signatureVerified)
            {
                return null;
            }

            var version = await ReadPlistValueAsync(plistPath, "CFBundleShortVersionString", cancellationToken).ConfigureAwait(false);
            var build = await ReadPlistValueAsync(plistPath, "CFBundleVersion", cancellationToken).ConfigureAwait(false);
            var executableFingerprint = await FingerprintFileAsync(executablePath, cancellationToken).ConfigureAwait(false);
            var plistFingerprint = await FingerprintFileAsync(plistPath, cancellationToken).ConfigureAwait(false);
            var nestedFingerprint = await FingerprintNestedCodeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var sourceFingerprint = ComputeSourceFingerprint(fullPath, identifier!, version, build, executableFingerprint, plistFingerprint, nestedFingerprint);
            return new MacBundleInfo(
                fullPath,
                identifier!,
                executablePath,
                version,
                build,
                signatureVerified,
                sourceFingerprint,
                executableFingerprint,
                plistFingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private bool IsApprovedLocation(string fullPath) =>
        _approvedLocations.Any(root => PathSafety.IsContainedBy(root, fullPath));

    private async Task<string?> ReadPlistValueAsync(
        string plistPath,
        string key,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            var result = await _commandRunner.RunAsync(
                "/usr/bin/plutil",
                ["-extract", key, "raw", "-o", "-", "--", plistPath],
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return result.StandardOutput.Trim();
            }
        }

        // XML fallback keeps bundle validation deterministic in headless tests on Windows.
        try
        {
            var document = XDocument.Load(plistPath, LoadOptions.PreserveWhitespace);
            var dict = document.Descendants("dict").FirstOrDefault();
            if (dict is null)
            {
                return null;
            }

            var elements = dict.Elements().ToArray();
            for (var index = 0; index + 1 < elements.Length; index += 2)
            {
                if (elements[index].Name.LocalName == "key"
                    && string.Equals(elements[index].Value, key, StringComparison.Ordinal))
                {
                    var value = elements[index + 1];
                    return value.Name.LocalName is "true" or "false"
                        ? value.Name.LocalName
                        : value.Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Binary plists are expected on macOS; the macOS plutil path above handles them.
        }

        return null;
    }

    private static async Task<string> FingerprintFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSourceFingerprint(
        string path,
        string identifier,
        string? version,
        string? build,
        string executableFingerprint,
        string plistFingerprint,
        string nestedFingerprint)
    {
        var value = string.Join("\n", path, identifier, version ?? string.Empty, build ?? string.Empty,
            executableFingerprint, plistFingerprint, nestedFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static async Task<string> FingerprintNestedCodeAsync(string bundlePath, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var paths = Directory.EnumerateFiles(bundlePath, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".framework", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xpc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(bundlePath, path);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public sealed class MacSignatureVerifier
{
    private readonly IMacProcessCommandRunner _commandRunner;

    public MacSignatureVerifier(IMacProcessCommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    public async Task<bool> VerifyAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true;
        }

        var result = await _commandRunner.RunAsync(
            "/usr/bin/codesign",
            ["--verify", "--deep", "--strict", "--verbose=2", "--", bundlePath],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        var details = await _commandRunner.RunAsync(
            "/usr/bin/codesign",
            ["--display", "--verbose=4", "--", bundlePath],
            cancellationToken).ConfigureAwait(false);
        var gatekeeper = await _commandRunner.RunAsync(
            "/usr/sbin/spctl",
            ["--assess", "--type", "execute", "--verbose=4", "--", bundlePath],
            cancellationToken).ConfigureAwait(false);
        var requirements = await _commandRunner.RunAsync(
            "/usr/bin/codesign",
            ["--display", "--requirements", "-", "--", bundlePath],
            cancellationToken).ConfigureAwait(false);
        return IsAcceptedOfficialBundleSignature(details, gatekeeper, requirements);
    }

    internal static bool IsAcceptedOfficialBundleSignature(
        MacProcessCommandResult details,
        MacProcessCommandResult gatekeeper,
        MacProcessCommandResult requirements)
    {
        var output = details.StandardOutput + "\n" + details.StandardError;
        var requirementOutput = requirements.StandardOutput + "\n" + requirements.StandardError;
        // The installed source must be a notarizable/Developer ID application with the
        // Roblox bundle identity. Ad-hoc or unsigned input is never accepted as the basis
        // for a managed runtime. Team ID pinning is intentionally omitted, so this check
        // does not independently prove the publisher's identity.
        return details.Succeeded
            && gatekeeper.Succeeded
            && requirements.Succeeded
            && output.Contains("Authority=Developer ID Application:", StringComparison.Ordinal)
            && output.Contains($"Identifier={MacBundleDiscovery.RobloxBundleIdentifier}", StringComparison.Ordinal)
            && requirementOutput.Contains($"identifier \"{MacBundleDiscovery.RobloxBundleIdentifier}\"", StringComparison.Ordinal)
            && !output.Contains("Signature=adhoc", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> VerifyManagedAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true;
        }

        var result = await _commandRunner.RunAsync(
            "/usr/bin/codesign",
            ["--verify", "--deep", "--strict", "--verbose=2", "--", bundlePath],
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public Task<MacProcessCommandResult> ExtractEntitlementsAsync(
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        return _commandRunner.RunAsync(
            "/usr/bin/codesign",
            ["-d", "--entitlements", ":-", "--", bundlePath],
            cancellationToken);
    }
}

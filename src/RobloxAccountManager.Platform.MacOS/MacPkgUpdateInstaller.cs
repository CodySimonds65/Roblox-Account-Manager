using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacPkgTrustConfiguration(
    string ExpectedInstallerIdentity,
    string ExpectedPackageIdentifier,
    string ExpectedBundleIdentifier,
    string ExpectedExecutableName,
    bool AllowUnsignedPackages = false)
{
    public void Validate()
    {
        if ((!AllowUnsignedPackages && string.IsNullOrWhiteSpace(ExpectedInstallerIdentity))
            || string.IsNullOrWhiteSpace(ExpectedPackageIdentifier)
            || string.IsNullOrWhiteSpace(ExpectedBundleIdentifier)
            || string.IsNullOrWhiteSpace(ExpectedExecutableName)
            || ExpectedInstallerIdentity.Contains('\n')
            || ExpectedInstallerIdentity.Contains('\r')
            || ExpectedPackageIdentifier.Contains('\n')
            || ExpectedPackageIdentifier.Contains('\r')
            || ExpectedBundleIdentifier.Contains('\n')
            || ExpectedBundleIdentifier.Contains('\r')
            || ExpectedExecutableName.Contains('\n'))
        {
            throw new ArgumentException("Complete trusted macOS PKG identity configuration is required.");
        }
    }
}

/// <summary>
/// Verifies and hands a downloaded PKG to Apple's Installer application. The Installer process
/// owns privilege escalation; RAM never ships or starts a custom privileged helper.
/// </summary>
public sealed class MacPkgUpdateInstaller : Contracts.IPlatformUpdateInstaller
{
    private const string SystemInstallerApplication = "/System/Library/CoreServices/Installer.app";
    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly string _expectedRid;
    private readonly MacPkgTrustConfiguration _trust;
    private readonly string _stagingRoot;
    private readonly ulong _currentPackageVersion;
    private readonly Version _currentAppVersion;

    public MacPkgUpdateInstaller(
        IMacProcessCommandRunner? commandRunner = null,
        string? expectedRid = null,
        MacPkgTrustConfiguration? trust = null,
        string? stagingRoot = null,
        string? currentPackageVersion = null,
        string? currentAppVersion = null)
    {
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _expectedRid = expectedRid ?? GetCurrentRid();
        _trust = trust ?? throw new ArgumentException("Trusted PKG signer and payload identity configuration is required.", nameof(trust));
        _trust.Validate();
        var installedVersion = currentPackageVersion ?? ReadInstalledPackageVersion();
        if (!TryParsePackageVersion(installedVersion, out _currentPackageVersion))
        {
            throw new ArgumentException("A numeric installed PKG version is required.", nameof(currentPackageVersion));
        }
        var installedAppVersion = currentAppVersion ?? ReadInstalledAppVersion();
        if (!Version.TryParse(installedAppVersion, out var parsedAppVersion) || parsedAppVersion is null)
        {
            throw new ArgumentException("A valid installed semantic app version is required.", nameof(currentAppVersion));
        }
        _currentAppVersion = parsedAppVersion;
        _stagingRoot = Path.GetFullPath(stagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "RobloxAccountManager", "Updates", "verified"));
        CleanupStaleStagedPackages();
    }

    public Contracts.RobloxPlatform Platform => Contracts.RobloxPlatform.MacOS;

    public async ValueTask<Contracts.UpdateInstallResult> InstallAsync(
        Contracts.UpdatePackage package,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return Contracts.UpdateInstallResult.Rejected("platform-not-supported");
        }

        if (!userConfirmed)
        {
            return Contracts.UpdateInstallResult.Rejected("confirmation-required");
        }

        var prepared = await PrepareVerifiedPackageAsync(package, cancellationToken).ConfigureAwait(false);
        if (prepared.Error is not null)
        {
            return Contracts.UpdateInstallResult.Rejected(prepared.Error);
        }

        var stagedPackage = package with { LocalPath = prepared.Path! };

        // ArgumentList preserves the package path as one argument. No shell or command-string
        // interpolation is used, and the URI/path is never written to diagnostics.
        var launch = await _commandRunner.RunAsync(
            "/usr/bin/open",
            BuildOpenArguments(stagedPackage),
            cancellationToken).ConfigureAwait(false);
        return launch.Succeeded
            ? Contracts.UpdateInstallResult.InstallerOpened()
            : Contracts.UpdateInstallResult.Rejected("installer-launch-failed");
    }

    internal async Task<(string? Path, string? Error)> PrepareVerifiedPackageAsync(
        Contracts.UpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        var prevalidation = ValidateMetadata(package);
        if (prevalidation is not null)
        {
            return (null, prevalidation);
        }

        var staged = await StagePackageAsync(package, cancellationToken).ConfigureAwait(false);
        if (staged.Error is not null)
        {
            return staged;
        }

        var stagedPackage = package with { LocalPath = staged.Path! };
        var validation = await ValidateLocalPackageAsync(stagedPackage, cancellationToken).ConfigureAwait(false);
        if (validation is not null)
        {
            DeleteStagedPackage(staged.Path);
            return (null, validation);
        }

        return staged;
    }

    public async Task<string?> ValidateAsync(
        Contracts.UpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        var metadata = ValidateMetadata(package);
        if (metadata is not null)
        {
            return metadata;
        }

        return await ValidateLocalPackageAsync(package, cancellationToken).ConfigureAwait(false);
    }

    private string? ValidateMetadata(Contracts.UpdatePackage package)
    {
        if (package.Platform != Contracts.RobloxPlatform.MacOS)
        {
            return "platform-mismatch";
        }

        if (!string.Equals(package.Rid, _expectedRid, StringComparison.Ordinal))
        {
            return "rid-mismatch";
        }

        if (!package.PackageUri.IsAbsoluteUri
            || !string.Equals(package.PackageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "https-required";
        }

        if (string.IsNullOrWhiteSpace(package.LocalPath)
            || !string.Equals(Path.GetExtension(package.LocalPath), ".pkg", StringComparison.OrdinalIgnoreCase))
        {
            return "pkg-path-required";
        }

        if (!TryParsePackageVersion(package.PackageVersion, out var packageVersion)
            || packageVersion <= _currentPackageVersion)
        {
            return "pkg-version-not-newer";
        }

        if (package.Version <= _currentAppVersion)
        {
            return "app-version-not-newer";
        }

        return null;
    }

    private async Task<string?> ValidateLocalPackageAsync(
        Contracts.UpdatePackage package,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(package.LocalPath);
        if (!File.Exists(fullPath))
        {
            return "pkg-not-found";
        }

        try
        {
            PathSafety.RejectSymlinkComponents(fullPath);
            PathSafety.RejectSymlink(fullPath);
        }
        catch (InvalidOperationException)
        {
            return "pkg-path-invalid";
        }

        if (!IsSha256(package.Sha256))
        {
            return "sha256-invalid";
        }

        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var expected = Convert.FromHexString(package.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                return "sha256-mismatch";
            }
        }

        var signature = await _commandRunner.RunAsync(
            "/usr/sbin/pkgutil",
            ["--check-signature", fullPath],
            cancellationToken).ConfigureAwait(false);
        var signatureOutput = signature.StandardOutput + "\n" + signature.StandardError;
        var signatureIsTrusted = signature.Succeeded
            && signatureOutput.Contains("Status: signed by a certificate trusted by Gatekeeper", StringComparison.OrdinalIgnoreCase)
            && signatureOutput.Contains("Developer ID Installer:", StringComparison.OrdinalIgnoreCase);
        if (!signatureIsTrusted)
        {
            if (!(package.IsUnsigned && _trust.AllowUnsignedPackages))
                return "pkg-signature-invalid";
        }
        else
        {
            // Development mode only permits a genuinely unsigned package when the
            // manifest explicitly labels it unsigned. It must never turn off the
            // signer-identity check for a package that does carry a trusted signature.
            if (string.IsNullOrWhiteSpace(_trust.ExpectedInstallerIdentity)
                || string.Equals(_trust.ExpectedInstallerIdentity, "unsigned-development", StringComparison.OrdinalIgnoreCase))
            {
                return "installer-identity-mismatch";
            }

            var expectedSignerLine = _trust.ExpectedInstallerIdentity.StartsWith(
                "Developer ID Installer:",
                StringComparison.Ordinal)
                ? _trust.ExpectedInstallerIdentity.Trim()
                : "Developer ID Installer: " + _trust.ExpectedInstallerIdentity;
            if (!signatureOutput.Split('\n').Any(line => string.Equals(line.Trim(), expectedSignerLine, StringComparison.Ordinal)))
                return "installer-identity-mismatch";
        }

        var expanded = await InspectExpandedPackageAsync(fullPath, package, cancellationToken).ConfigureAwait(false);
        if (expanded is not null)
        {
            return expanded;
        }

        return null;
    }

    private async Task<(string? Path, string? Error)> StagePackageAsync(
        Contracts.UpdatePackage package,
        CancellationToken cancellationToken)
    {
        try
        {
            PathSafety.EnsureOwnerOnlyDirectory(_stagingRoot);
            var stagedPath = PathSafety.RequireContainedPath(
                _stagingRoot,
                Path.Combine(_stagingRoot, $"update-{Guid.NewGuid():N}.pkg"));
            var sourcePath = Path.GetFullPath(package.LocalPath);
            PathSafety.RejectSymlinkComponents(sourcePath);
            PathSafety.RejectSymlink(sourcePath);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
            await using (var destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            PathSafety.RejectSymlinkComponents(stagedPath);
            PathSafety.RejectSymlink(stagedPath);
            return (stagedPath, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, "pkg-staging-failed");
        }
    }

    private void DeleteStagedPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            PathSafety.RejectSymlinkComponents(path);
            PathSafety.RejectSymlink(path);
            if (PathSafety.IsContainedBy(_stagingRoot, path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Cleanup is fail-closed; a path that changed under us is left in place rather than
            // risking deletion outside the owner-only staging root.
        }
    }

    private void CleanupStaleStagedPackages()
    {
        try
        {
            PathSafety.EnsureOwnerOnlyDirectory(_stagingRoot);
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var path in Directory.EnumerateFiles(_stagingRoot, "*.pkg", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                PathSafety.RejectSymlinkComponents(path);
                PathSafety.RejectSymlink(path);
                if (PathSafety.IsContainedBy(_stagingRoot, path))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Stale cleanup is best-effort. Never broaden the cleanup target when validation fails.
        }
    }

    private async Task<string?> InspectExpandedPackageAsync(
        string packagePath,
        Contracts.UpdatePackage package,
        CancellationToken cancellationToken)
    {
        var expansionParent = PathSafety.RequireContainedPath(
            _stagingRoot,
            Path.Combine(_stagingRoot, $"inspect-{Guid.NewGuid():N}"));
        PathSafety.EnsureOwnerOnlyDirectory(expansionParent);
        var expansionRoot = PathSafety.RequireContainedPath(expansionParent, Path.Combine(expansionParent, "expanded"));
        try
        {
            var expanded = await _commandRunner.RunAsync(
                "/usr/sbin/pkgutil",
                ["--expand-full", packagePath, expansionRoot],
                cancellationToken).ConfigureAwait(false);
            if (!expanded.Succeeded)
            {
                return "pkg-payload-uninspectable";
            }

            var unsafePayload = RejectUnsafeExpandedEntries(expansionRoot);
            if (unsafePayload is not null)
            {
                return unsafePayload;
            }

            if (Directory.EnumerateDirectories(expansionRoot, "*", SearchOption.AllDirectories)
                    .Any(path => string.Equals(Path.GetFileName(path), "Scripts", StringComparison.OrdinalIgnoreCase))
                || Directory.EnumerateFiles(expansionRoot, "*.sh", SearchOption.AllDirectories).Any()
                || Directory.EnumerateFiles(expansionRoot, "preinstall", SearchOption.AllDirectories).Any()
                || Directory.EnumerateFiles(expansionRoot, "postinstall", SearchOption.AllDirectories).Any())
            {
                return "pkg-installer-scripts-not-allowed";
            }

            var packageInfos = Directory.EnumerateFiles(expansionRoot, "PackageInfo", SearchOption.AllDirectories).ToArray();
            if (packageInfos.Length != 1)
            {
                return packageInfos.Length == 0 ? "pkg-identity-missing" : "pkg-multiple-components-not-allowed";
            }

            var matchedIdentity = false;
            foreach (var packageInfo in packageInfos)
            {
                var xml = await File.ReadAllTextAsync(packageInfo, cancellationToken).ConfigureAwait(false);
                try
                {
                    var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                    var root = document.Root;
                    if (document.Descendants().Any(element =>
                            string.Equals(element.Name.LocalName, "scripts", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(element.Name.LocalName, "script", StringComparison.OrdinalIgnoreCase)
                            || element.Name.LocalName.EndsWith("install", StringComparison.OrdinalIgnoreCase)))
                    {
                        return "pkg-installer-scripts-not-allowed";
                    }
                    matchedIdentity |= string.Equals(root?.Attribute("identifier")?.Value,
                        _trust.ExpectedPackageIdentifier, StringComparison.Ordinal);
                    if (matchedIdentity)
                    {
                        var packageInstallLocation = root?.Attribute("install-location")?.Value;
                        if (packageInstallLocation is not ("/" or "/Applications"))
                        {
                            return "pkg-install-location-mismatch";
                        }

                        // The package's internal version is checked below against the signed
                        // update manifest's requested version; do not accept a versionless node.
                        if (root?.Attribute("version") is null)
                        {
                            return "pkg-version-missing";
                        }
                    }
                }
                catch (System.Xml.XmlException)
                {
                    return "pkg-identity-unparseable";
                }
            }

            if (!matchedIdentity)
            {
                return "pkg-identity-mismatch";
            }

            // PackageInfo version is inspected from the expanded payload. The installer does not
            // treat a filename/tag as proof of the package version.
            var versionMatches = packageInfos.Any(path =>
            {
                try
                {
                    var root = XDocument.Parse(File.ReadAllText(path)).Root;
                    return string.Equals(root?.Attribute("identifier")?.Value, _trust.ExpectedPackageIdentifier, StringComparison.Ordinal)
                        && string.Equals(root?.Attribute("version")?.Value, package.PackageVersion, StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            });
            if (!versionMatches)
            {
                return "pkg-version-mismatch";
            }

            var payloadValidation = ValidatePayloadTree(expansionRoot);
            if (payloadValidation.Error is not null)
            {
                return payloadValidation.Error;
            }

            var installLocation = packageInfos
                .Select(path =>
                {
                    try
                    {
                        var root = XDocument.Parse(File.ReadAllText(path)).Root;
                        return string.Equals(root?.Attribute("identifier")?.Value,
                            _trust.ExpectedPackageIdentifier, StringComparison.Ordinal)
                            ? root?.Attribute("install-location")?.Value
                            : null;
                    }
                    catch
                    {
                        return null;
                    }
                })
                .FirstOrDefault(location => location is not null);
            var expectedAppRoot = installLocation == "/"
                ? Path.Combine(expansionRoot, "Payload", "Applications", "Roblox Account Manager.app")
                : Path.Combine(expansionRoot, "Payload", "Roblox Account Manager.app");
            if (!string.Equals(payloadValidation.AppRoot, expectedAppRoot, StringComparison.Ordinal))
            {
                return "pkg-install-layout-mismatch";
            }

            var plistPath = Path.Combine(payloadValidation.AppRoot!, "Contents", "Info.plist");
            if (!File.Exists(plistPath))
            {
                return "pkg-payload-app-missing";
            }
            PathSafety.RejectSymlinkComponents(plistPath);
            PathSafety.RejectSymlink(plistPath);

            var plistDocument = XDocument.Parse(await File.ReadAllTextAsync(plistPath, cancellationToken).ConfigureAwait(false));
            var plistValues = plistDocument.Root?.Element("dict")?.Elements().ToArray() ?? [];
            var plistMap = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index + 1 < plistValues.Length; index += 2)
            {
                var key = plistValues[index];
                var value = plistValues[index + 1];
                if (key.Name.LocalName == "key" && value.Name.LocalName == "string")
                {
                    plistMap[key.Value] = value.Value;
                }
            }

            if (!plistMap.TryGetValue("CFBundleIdentifier", out var bundleIdentifier)
                || !string.Equals(bundleIdentifier, _trust.ExpectedBundleIdentifier, StringComparison.Ordinal))
            {
                return "pkg-bundle-identity-mismatch";
            }

            if (!plistMap.TryGetValue("CFBundleExecutable", out var bundleExecutable)
                || !string.Equals(bundleExecutable, _trust.ExpectedExecutableName, StringComparison.Ordinal))
            {
                return "pkg-executable-mismatch";
            }

            if (!plistMap.TryGetValue("CFBundleShortVersionString", out var bundleVersion)
                || !string.Equals(bundleVersion, package.Version.ToString(), StringComparison.Ordinal))
            {
                return "pkg-app-version-mismatch";
            }

            var executable = Path.Combine(
                payloadValidation.AppRoot!,
                "Contents",
                "MacOS",
                _trust.ExpectedExecutableName);
            if (!File.Exists(executable))
            {
                return "pkg-executable-missing";
            }
            PathSafety.RejectSymlinkComponents(executable);
            PathSafety.RejectSymlink(executable);

            var lipo = await _commandRunner.RunAsync(
                "/usr/bin/lipo",
                ["-info", executable],
                cancellationToken).ConfigureAwait(false);
            var architectureOutput = lipo.StandardOutput + "\n" + lipo.StandardError;
            var architecture = _expectedRid == "osx-arm64" ? "arm64" : "x86_64";
            if (!lipo.Succeeded || !architectureOutput.Contains(architecture, StringComparison.OrdinalIgnoreCase))
            {
                return "pkg-architecture-mismatch";
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
        {
            return "pkg-payload-uninspectable";
        }
        finally
        {
            if (Directory.Exists(expansionParent) && PathSafety.IsContainedBy(_stagingRoot, expansionParent))
            {
                try { Directory.Delete(expansionParent, recursive: true); } catch { }
            }
        }
    }

    private static string? RejectUnsafeExpandedEntries(string root)
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

        return null;
    }

    private static (string? AppRoot, string? Error) ValidatePayloadTree(string expansionRoot)
    {
        var payloadRoot = Path.Combine(expansionRoot, "Payload");
        if (!Directory.Exists(payloadRoot))
        {
            return (null, "pkg-payload-missing");
        }

        var appRoots = new[]
        {
            Path.Combine(payloadRoot, "Applications", "Roblox Account Manager.app"),
            Path.Combine(payloadRoot, "Roblox Account Manager.app")
        };
        var appRoot = appRoots.FirstOrDefault(Directory.Exists);
        if (appRoot is null)
        {
            return (null, "pkg-payload-app-missing");
        }

        var allowedRoots = appRoots
            .Where(Directory.Exists)
            .Select(path => Path.GetRelativePath(payloadRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        var pending = new Stack<string>([payloadRoot]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var relative = Path.GetRelativePath(payloadRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("/", StringComparison.Ordinal)
                    || relative.Split('/').Any(part => part is "" or "." or ".."))
                {
                    return (null, "pkg-payload-path-invalid");
                }

                var isAllowed = allowedRoots.Any(root => relative.Equals(root, StringComparison.Ordinal)
                    || relative.StartsWith(root + "/", StringComparison.Ordinal))
                    || string.Equals(relative, "Applications", StringComparison.Ordinal);
                if (!isAllowed)
                {
                    return (null, "pkg-payload-unexpected-path");
                }

                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }

        return (appRoot, null);
    }

    public static string GetCurrentRid()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "osx-arm64",
            Architecture.X64 => "osx-x64",
            _ => "unsupported"
        };
    }

    public static IReadOnlyList<string> BuildOpenArguments(Contracts.UpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return ["-a", SystemInstallerApplication, "--", package.LocalPath];
    }

    private static bool TryParsePackageVersion(string? value, out ulong version) =>
        ulong.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version)
        && version > 0;

    private static string? ReadInstalledPackageVersion()
        => ReadInstalledPlistValue("CFBundleVersion");

    private static string? ReadInstalledAppVersion()
        => ReadInstalledPlistValue("CFBundleShortVersionString");

    private static string? ReadInstalledPlistValue(string keyName)
    {
        try
        {
            var plistPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Info.plist"));
            if (!File.Exists(plistPath))
            {
                return null;
            }

            var document = XDocument.Load(plistPath);
            var values = document.Root?.Element("dict")?.Elements().ToArray() ?? [];
            for (var index = 0; index + 1 < values.Length; index += 2)
            {
                if (values[index].Name.LocalName == "key"
                    && values[index].Value == keyName
                    && values[index + 1].Name.LocalName == "string")
                {
                    return values[index + 1].Value;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        return null;
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            return false;
        }

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
}

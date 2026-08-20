using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Core.Contracts;

/// <summary>Verified, architecture-specific update metadata shared by all desktop frontends.</summary>
public sealed record UpdatePackage(
    RobloxPlatform Platform,
    string Rid,
    Version Version,
    string PackageVersion,
    Uri PackageUri,
    string Sha256,
    string LocalPath,
    bool IsUnsigned = false);

public sealed record UpdateInstallResult(
    bool Accepted,
    string DiagnosticCode)
{
    /// <summary>
    /// The platform installer accepted the handoff. This does not claim that the
    /// separate installer process has finished copying the application bundle.
    /// </summary>
    public static UpdateInstallResult InstallerOpened() => new(true, "installer-opened");

    // Keep the legacy diagnostic stable for platform adapters outside this
    // repository. New code should use InstallerOpened() when it has only
    // handed the package to a separate installer process.
    public static UpdateInstallResult Success() => new(true, "installer-started");

    public static UpdateInstallResult Rejected(string code) => new(false, code);
}

public interface IPlatformUpdateInstaller
{
    RobloxPlatform Platform { get; }

    ValueTask<UpdateInstallResult> InstallAsync(
        UpdatePackage package,
        bool userConfirmed,
        CancellationToken cancellationToken = default);
}

/// <summary>Downloads and validates the latest package for one explicit update channel.</summary>
public interface IPlatformUpdateSource
{
    RobloxPlatform Platform { get; }

    ValueTask<UpdatePackage?> DownloadLatestAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default);
}

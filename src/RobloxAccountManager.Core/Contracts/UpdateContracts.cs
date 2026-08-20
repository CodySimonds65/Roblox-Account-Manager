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

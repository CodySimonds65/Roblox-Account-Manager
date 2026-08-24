using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Core.Launch;

/// <summary>
/// Serializes launches across accounts. A new launch URI is requested for every
/// attempt, including verification retries; this is important because Roblox
/// authentication tickets are single-use and must never be replayed.
/// </summary>
public sealed class SerializedLaunchCoordinator
{
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly IRobloxProcessLocator _processLocator;
    private readonly IRobloxMultiInstanceStrategy _multiInstanceStrategy;
    private readonly IRobloxPlatformLauncher _platformLauncher;

    public SerializedLaunchCoordinator(
        IRobloxProcessLocator processLocator,
        IRobloxMultiInstanceStrategy multiInstanceStrategy,
        IRobloxPlatformLauncher platformLauncher)
    {
        _processLocator = processLocator ?? throw new ArgumentNullException(nameof(processLocator));
        _multiInstanceStrategy = multiInstanceStrategy ?? throw new ArgumentNullException(nameof(multiInstanceStrategy));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
    }

    public async ValueTask<LaunchResult> LaunchAsync(
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AccountId))
            throw new ArgumentException("An account identifier is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.FreshUriFactory);
        if (request.MaxAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(request), "Attempts must be between 1 and 10.");
        if (_multiInstanceStrategy.Platform != _platformLauncher.Platform)
            throw new InvalidOperationException("The multi-instance and launcher platforms must match.");

        await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var attempts = new List<LaunchAttemptDiagnostic>(request.MaxAttempts);
        var preparationLeases = new List<IAsyncDisposable>(request.MaxAttempts);
        try
        {
            for (var attempt = 1; attempt <= request.MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preparation = await _multiInstanceStrategy.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
                if (!preparation.Succeeded)
                {
                    var failureKind = preparation.FailureKind == LaunchFailureKind.None
                        ? LaunchFailureKind.LauncherRejected
                        : preparation.FailureKind;
                    attempts.Add(new LaunchAttemptDiagnostic(
                        attempt,
                        failureKind,
                        preparation.DiagnosticCode ?? "launch-preparation-failed",
                        BundlePath: request.RobloxBundlePath));
                    return new LaunchResult(false, null, attempts, failureKind);
                }

                if (preparation.Lease is not null)
                    preparationLeases.Add(preparation.Lease);
                var preparedRequest = preparation.Request;

                // Preparation can clone and sign a macOS runtime. Capture the
                // baseline only after that potentially slow work and against
                // the effective bundle that will actually be launched.
                var snapshot = await _processLocator.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var release = await _multiInstanceStrategy.ReleaseSingletonAsync(cancellationToken).ConfigureAwait(false);
                if (!release.Succeeded)
                {
                    attempts.Add(new LaunchAttemptDiagnostic(
                        attempt,
                        LaunchFailureKind.LauncherRejected,
                        release.DiagnosticCode ?? "singleton-release-failed",
                        BundlePath: preparedRequest.RobloxBundlePath,
                        SingletonStatus: release.Status,
                        NativeError: release.NativeError));
                    if (release.Status is SingletonReleaseStatus.PermissionDenied or SingletonReleaseStatus.NotSupported)
                        return new LaunchResult(false, null, attempts, LaunchFailureKind.LauncherRejected);
                    continue;
                }

                // Acquire a one-use ticket only after every potentially slow
                // preparation step. This call remains inside the retry loop.
                var freshUri = await preparedRequest.FreshUriFactory(cancellationToken).ConfigureAwait(false);
                if (!IsRobloxLaunchUri(freshUri))
                {
                    attempts.Add(new LaunchAttemptDiagnostic(attempt, LaunchFailureKind.LauncherRejected, "invalid-launch-uri"));
                    continue;
                }

                var launch = await _platformLauncher.LaunchAsync(preparedRequest, freshUri, cancellationToken).ConfigureAwait(false);
                if (!launch.Accepted)
                {
                    attempts.Add(new LaunchAttemptDiagnostic(
                        attempt,
                        launch.FailureKind == LaunchFailureKind.None ? LaunchFailureKind.LauncherRejected : launch.FailureKind,
                        launch.DiagnosticCode ?? "launcher-rejected",
                        BundlePath: preparedRequest.RobloxBundlePath,
                        SingletonStatus: release.Status,
                        NativeError: release.NativeError));
                    continue;
                }

                var verificationRequest = string.IsNullOrWhiteSpace(launch.ValidatedRobloxBundleFingerprint)
                    ? preparedRequest
                    : preparedRequest with { ValidatedRobloxBundleFingerprint = launch.ValidatedRobloxBundleFingerprint };
                var verification = await _processLocator.VerifyLaunchedProcessAsync(snapshot, verificationRequest, cancellationToken).ConfigureAwait(false);
                if (verification.Succeeded && verification.Process is not null && verification.Process.Identity.IsValid)
                {
                    var identity = verification.Process.Identity;
                    attempts.Add(new LaunchAttemptDiagnostic(
                        attempt,
                        LaunchFailureKind.None,
                        "verified",
                        identity.Pid,
                        identity.StartTimeUtc,
                        identity.BundlePath ?? preparedRequest.RobloxBundlePath,
                        release.Status,
                        release.NativeError));
                    return new LaunchResult(true, verification.Process, attempts);
                }

                attempts.Add(new LaunchAttemptDiagnostic(
                    attempt,
                    verification.FailureKind == LaunchFailureKind.None ? LaunchFailureKind.VerificationFailed : verification.FailureKind,
                    verification.DiagnosticCode ?? "process-verification-failed",
                    BundlePath: preparedRequest.RobloxBundlePath,
                    SingletonStatus: release.Status,
                    NativeError: release.NativeError));
            }

            return new LaunchResult(false, null, attempts, LaunchFailureKind.RetryLimitReached);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LaunchResult.Cancelled(attempts);
        }
        finally
        {
            for (var index = preparationLeases.Count - 1; index >= 0; index--)
            {
                try
                {
                    await preparationLeases[index].DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A reservation cleanup failure must not replace the launch result.
                }
            }
            _launchGate.Release();
        }
    }

    private static bool IsRobloxLaunchUri(Uri uri) =>
        uri is not null &&
        (string.Equals(uri.Scheme, "roblox-player", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, "roblox", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Values suitable for logs. In particular, never write a captured URI.</summary>
public static class LaunchDiagnostics
{
    public static string SanitisePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        return Path.GetFullPath(path).Replace('\\', '/');
    }

    public static string SanitiseCode(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? "unknown"
            : new string(code.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').Take(96).ToArray());
}

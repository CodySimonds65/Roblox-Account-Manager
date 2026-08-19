using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

public sealed class MacCoreProcessLocator : Contracts.IRobloxProcessLocator
{
    private readonly MacRobloxProcessLocator _inner;

    public MacCoreProcessLocator(MacRobloxProcessLocator? inner = null)
    {
        _inner = inner ?? new MacRobloxProcessLocator();
    }

    public ValueTask<Contracts.RobloxLaunchSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _inner.CaptureSnapshot();
        return ValueTask.FromResult(new Contracts.RobloxLaunchSnapshot(
            snapshot.CapturedAt,
            snapshot.Processes.Select(process => ToCore(process.Identity)).ToArray()));
    }

    public async ValueTask<Contracts.LaunchVerificationResult> VerifyLaunchedProcessAsync(
        Contracts.RobloxLaunchSnapshot before,
        Contracts.RobloxLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        var beforeSnapshot = new RobloxLaunchSnapshot(
            before.CapturedAtUtc,
            before.Processes.Select(identity => new RobloxProcessInfo(
                FromCore(identity),
                "RobloxPlayer",
                _inner.FindProcess(identity.Pid)?.IsManaged == true,
                true)).ToArray());
        var expectedBundle = request.RobloxBundlePath;
        if (string.IsNullOrWhiteSpace(expectedBundle))
        {
            return Contracts.LaunchVerificationResult.Failure(Contracts.LaunchFailureKind.VerificationFailed, "bundle-path-required");
        }

        var result = await new MacLaunchVerificationService(_inner).WaitForNewProcessAsync(
            beforeSnapshot,
            expectedBundle,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.NewProcess is not null)
        {
            _inner.RegisterManaged(result.NewProcess.Identity);
            var managed = ToCoreInfo(result.NewProcess) with { IsManaged = true, AccountId = request.AccountId };
            return new Contracts.LaunchVerificationResult(
                true,
                managed,
                Contracts.LaunchFailureKind.None,
                null,
                result.PriorManagedProcessDisappeared);
        }
        return Contracts.LaunchVerificationResult.Failure(
                result.Status == LaunchVerificationStatus.TimedOut
                    ? Contracts.LaunchFailureKind.ProcessNotFound
                    : Contracts.LaunchFailureKind.VerificationFailed,
                result.Status.ToString());
    }

    public ValueTask<IReadOnlyList<Contracts.RobloxProcessInfo>> GetManagedProcessesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Contracts.RobloxProcessInfo> result = _inner.CaptureSnapshot().Processes
            .Where(process => process.IsManaged)
            .Select(ToCoreInfo)
            .ToArray();
        return ValueTask.FromResult(result);
    }

    internal static Contracts.RobloxProcessInfo ToCoreInfo(RobloxProcessInfo process) =>
        new(ToCore(process.Identity), process.IsManaged, null, process.Identity.BundlePath);

    internal static Contracts.RobloxProcessIdentity ToCore(RobloxProcessIdentity identity) =>
        new(identity.ProcessId, identity.StartTime, identity.ExecutablePath, identity.BundlePath, Contracts.RobloxPlatform.MacOS);

    internal static RobloxProcessIdentity FromCore(Contracts.RobloxProcessIdentity identity) =>
        new(identity.Pid, identity.StartTimeUtc, identity.ExecutablePath, identity.BundlePath ?? string.Empty);
}

public sealed class MacCoreMultiInstanceStrategy : Contracts.IRobloxMultiInstanceStrategy
{
    private readonly MacSemaphore _semaphore;

    public MacCoreMultiInstanceStrategy(MacSemaphore? semaphore = null)
    {
        _semaphore = semaphore ?? new MacSemaphore();
    }

    public Contracts.RobloxPlatform Platform => Contracts.RobloxPlatform.MacOS;

    public ValueTask PrepareAsync(Contracts.RobloxLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.UserConsentedToMultiInstanceChanges)
            throw new InvalidOperationException("consent-required");
        return ValueTask.CompletedTask;
    }

    public ValueTask<Contracts.SingletonReleaseResult> ReleaseSingletonAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _semaphore.Unlink();
        var status = result.Status switch
        {
            SingletonReleaseStatus.Removed => Contracts.SingletonReleaseStatus.Released,
            SingletonReleaseStatus.AlreadyAbsent => Contracts.SingletonReleaseStatus.AlreadyAbsent,
            SingletonReleaseStatus.NotMacOS => Contracts.SingletonReleaseStatus.NotSupported,
            _ => Contracts.SingletonReleaseStatus.Failed
        };
        return ValueTask.FromResult(new Contracts.SingletonReleaseResult(status, result.NativeError, result.ErrorName));
    }

    public ValueTask<Contracts.MacLaunchLevel?> GetActiveMacLevelAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Contracts.MacLaunchLevel?>(Contracts.MacLaunchLevel.OriginalBundle);
}

public sealed class MacCorePlatformLauncher : Contracts.IRobloxPlatformLauncher
{
    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly MacSemaphore _semaphore;
    private readonly MacBundleDiscovery _bundleDiscovery;

    public MacCorePlatformLauncher(
        MacBundleDiscovery bundleDiscovery,
        IMacProcessCommandRunner? commandRunner = null,
        MacSemaphore? semaphore = null)
    {
        _bundleDiscovery = bundleDiscovery ?? throw new ArgumentNullException(nameof(bundleDiscovery));
        if (!_bundleDiscovery.HasTrustedTeamIdentifier)
            throw new ArgumentException("trusted-source-team-id-required", nameof(bundleDiscovery));
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _semaphore = semaphore ?? new MacSemaphore();
    }

    public Contracts.RobloxPlatform Platform => Contracts.RobloxPlatform.MacOS;

    public async ValueTask<Contracts.PlatformLaunchResult> LaunchAsync(
        Contracts.RobloxLaunchRequest request,
        Uri freshLaunchUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RobloxBundlePath))
        {
            return new Contracts.PlatformLaunchResult(false, Contracts.LaunchFailureKind.LauncherRejected, "bundle-path-required");
        }
        if (!OperatingSystem.IsMacOS())
            return new Contracts.PlatformLaunchResult(false, Contracts.LaunchFailureKind.PlatformNotSupported, "platform-not-supported");

        // Validate before the ticket-bearing URI is passed to any external process. Bundle id,
        // approved location, Developer ID chain, Gatekeeper assessment, designated requirement,
        // and the configured Roblox TeamIdentifier must all match.
        var validatedBundle = await _bundleDiscovery.ValidateAsync(
            request.RobloxBundlePath,
            cancellationToken).ConfigureAwait(false);
        if (validatedBundle is null)
            return new Contracts.PlatformLaunchResult(false, Contracts.LaunchFailureKind.LauncherRejected, "untrusted-roblox-bundle");
        try
        {
            var command = await _commandRunner.RunAsync(
                "/usr/bin/open",
                ["-n", "-a", validatedBundle.BundlePath, freshLaunchUri.AbsoluteUri],
                cancellationToken).ConfigureAwait(false);
            return command.Succeeded
                ? Contracts.PlatformLaunchResult.Success()
                : new Contracts.PlatformLaunchResult(false, Contracts.LaunchFailureKind.LauncherRejected, "macos-open-failed");
        }
        finally
        {
            // The matching pre-launch unlink is performed by the Core multi-
            // instance strategy. Always repeat it after open, including errors
            // and cancellation, without retaining the ticket-bearing URI.
            _ = _semaphore.Unlink();
        }
    }
}

public sealed class MacCoreClientWindowManager : Contracts.IClientWindowManager
{
    private readonly MacAccessibilityWindowManager _inner;
    private readonly MacCoreProcessLocator _locator;

    public MacCoreClientWindowManager(
        MacAccessibilityWindowManager? inner = null,
        MacCoreProcessLocator? locator = null)
    {
        _inner = inner ?? new MacAccessibilityWindowManager();
        _locator = locator ?? new MacCoreProcessLocator();
    }

    public async ValueTask<IReadOnlyList<Contracts.RobloxWindowInfo>> GetWindowsAsync(CancellationToken cancellationToken = default)
    {
        var processes = await _locator.GetManagedProcessesAsync(cancellationToken).ConfigureAwait(false);
        return processes.Select(process => new Contracts.RobloxWindowInfo(process.Identity, null, null)).ToArray();
    }

    public async ValueTask<bool> FocusAsync(Contracts.RobloxWindowInfo window, CancellationToken cancellationToken = default)
    {
        var result = await _inner.FocusAsync(MacCoreProcessLocator.FromCore(window.Process), cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async ValueTask<bool> TileAsync(IReadOnlyList<Contracts.RobloxWindowInfo> windows, CancellationToken cancellationToken = default)
    {
        var result = await _inner.TileAsync(windows.Select(window => MacCoreProcessLocator.FromCore(window.Process)).ToArray(), cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async ValueTask CloseAsync(Contracts.RobloxProcessInfo process, CancellationToken cancellationToken = default)
    {
        if (!process.IsManaged)
            throw new InvalidOperationException("unmanaged-process");
        _ = await _inner.CloseVerifiedAsync(MacCoreProcessLocator.FromCore(process.Identity), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MacCorePlatformCapabilities : Contracts.IPlatformCapabilities
{
    private readonly Contracts.PlatformCapabilitySnapshot _snapshot = new(
        Contracts.RobloxPlatform.MacOS,
        [
            new Contracts.CapabilityDescriptor("external-client-windows", Contracts.CapabilityStatus.Supported, "Roblox clients run as external macOS windows."),
            new Contracts.CapabilityDescriptor("window-focus-and-tiling", Contracts.CapabilityStatus.RequiresPermission, "Requires macOS Accessibility permission."),
            new Contracts.CapabilityDescriptor("input-automation", Contracts.CapabilityStatus.Unsupported, "Synthetic input is not implemented on macOS.", "platform-not-supported"),
            new Contracts.CapabilityDescriptor("screen-reading", Contracts.CapabilityStatus.Unsupported, "Screen reading is not implemented on macOS.", "platform-not-supported"),
            new Contracts.CapabilityDescriptor("native-embedding", Contracts.CapabilityStatus.Unsupported, "Roblox is managed as an external process on macOS.", "platform-not-supported")
        ]);

    public Contracts.RobloxPlatform Platform => Contracts.RobloxPlatform.MacOS;

    public Contracts.PlatformCapabilitySnapshot Snapshot => _snapshot;

    public Contracts.CapabilityDescriptor Get(string capabilityName) => _snapshot[capabilityName];
}

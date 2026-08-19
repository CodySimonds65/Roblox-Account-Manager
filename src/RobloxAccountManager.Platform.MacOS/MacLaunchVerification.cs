namespace RobloxAccountManager.Platform.MacOS;

public sealed class MacLaunchVerificationService
{
    private readonly IRobloxProcessLocator _processLocator;

    public MacLaunchVerificationService(IRobloxProcessLocator processLocator)
    {
        _processLocator = processLocator;
    }

    public async Task<LaunchVerificationResult> WaitForNewProcessAsync(
        RobloxLaunchSnapshot before,
        string expectedBundlePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedBundlePath) || !Directory.Exists(expectedBundlePath))
        {
            return new LaunchVerificationResult(
                LaunchVerificationStatus.InvalidBundle,
                null,
                false,
                ["The selected Roblox bundle no longer exists."]);
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        var candidates = new Dictionary<int, RobloxProcessInfo>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _processLocator.CaptureSnapshot();
            foreach (var process in snapshot.Processes)
            {
                if (before.Contains(process.Identity)
                    || !PathSafety.PathsEqual(process.Identity.BundlePath, expectedBundlePath)
                    || !process.IsStable)
                {
                    continue;
                }

                candidates[process.ProcessId] = process;
            }

            // A process is not a success merely because it appeared in one process snapshot.
            // Requiring it in a second snapshot avoids assigning a slow/exiting process.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            var stableSnapshot = _processLocator.CaptureSnapshot();
            var stableCandidates = new List<RobloxProcessInfo>();
            foreach (var candidate in candidates.Values)
            {
                var stable = stableSnapshot.Processes.FirstOrDefault(
                    process => _processLocator.IsSameProcess(candidate.Identity, process));
                if (stable is not null)
                {
                    stableCandidates.Add(stable);
                }
            }

            if (stableCandidates.Count > 1)
            {
                return new LaunchVerificationResult(
                    LaunchVerificationStatus.Failed,
                    null,
                    false,
                    ["Multiple new Roblox process identities appeared; no account was assigned."]);
            }

            if (stableCandidates.Count == 1)
            {
                var priorMissing = before.Processes.Any(
                    process => process.IsManaged
                        && PathSafety.PathsEqual(process.Identity.BundlePath, expectedBundlePath)
                        && (_processLocator.FindProcess(process.ProcessId) is not { } current
                            || !_processLocator.IsSameProcess(process.Identity, current)));
                    var warnings = priorMissing
                        ? ["A previously managed Roblox process disappeared before launch verification completed."]
                        : Array.Empty<string>();
                    return new LaunchVerificationResult(
                        LaunchVerificationStatus.Verified,
                        stableCandidates[0],
                        priorMissing,
                        warnings);
            }

            if (stableSnapshot.Processes.Any(
                    process => PathSafety.PathsEqual(process.Identity.BundlePath, expectedBundlePath)
                        && before.Contains(process.Identity)))
            {
                // Existing clients may still be starting. Continue polling until timeout rather
                // than mistaking one of them for the newly launched client.
                continue;
            }
        }

        var finalSnapshot = _processLocator.CaptureSnapshot();
        var priorMissingAtTimeout = before.Processes.Any(
            process => process.IsManaged
                && PathSafety.PathsEqual(process.Identity.BundlePath, expectedBundlePath)
                && (_processLocator.FindProcess(process.ProcessId) is not { } current
                    || !_processLocator.IsSameProcess(process.Identity, current)));
        var status = finalSnapshot.Processes.Any(
            process => PathSafety.PathsEqual(process.Identity.BundlePath, expectedBundlePath))
            ? LaunchVerificationStatus.ExistingProcessOnly
            : LaunchVerificationStatus.TimedOut;
        return new LaunchVerificationResult(
            status,
            null,
            priorMissingAtTimeout,
            ["No new stable Roblox process identity was observed before the verification timeout."]);
    }
}

public sealed class MacOriginalBundleLaunchStrategy
{
    private readonly IRobloxProcessLocator _processLocator;
    private readonly MacSemaphore _semaphore;
    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly MacLaunchVerificationService _verification;
    private readonly MacManagedProcessRegistry _registry;

    public MacOriginalBundleLaunchStrategy(
        IRobloxProcessLocator processLocator,
        MacSemaphore? semaphore = null,
        IMacProcessCommandRunner? commandRunner = null,
        MacManagedProcessRegistry? registry = null)
    {
        _processLocator = processLocator;
        _semaphore = semaphore ?? new MacSemaphore();
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _verification = new MacLaunchVerificationService(processLocator);
        _registry = registry ?? new MacManagedProcessRegistry();
    }

    public async Task<MacLaunchResult> LaunchAsync(
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserConsentedToManagedCopy)
        {
            var consentWarning = new[] { "Explicit consent is required before starting Roblox multi-instance isolation." };
            var consentFailure = new LaunchVerificationResult(
                LaunchVerificationStatus.Failed,
                null,
                false,
                consentWarning);
            var denied = new SingletonReleaseResult(SingletonReleaseStatus.Failed, 0, "consent-required");
            return new MacLaunchResult(MacLaunchLevel.OriginalBundle, denied, denied, consentFailure, false, consentWarning);
        }

        if (!OperatingSystem.IsMacOS())
        {
            var warning = new[] { "The original-bundle macOS launcher is unavailable on this platform." };
            var unsupported = new LaunchVerificationResult(LaunchVerificationStatus.Failed, null, false, warning);
            var notMac = new SingletonReleaseResult(SingletonReleaseStatus.NotMacOS, 0, null);
            return new MacLaunchResult(MacLaunchLevel.OriginalBundle, notMac, notMac, unsupported, false, warning);
        }

        var before = _processLocator.CaptureSnapshot();
        var beforeUnlink = _semaphore.Unlink();
        SingletonReleaseResult afterUnlink = beforeUnlink;
        LaunchVerificationResult verification;
        var commandSucceeded = false;
        try
        {
            if (!beforeUnlink.Succeeded)
            {
                verification = new LaunchVerificationResult(
                    LaunchVerificationStatus.Failed,
                    null,
                    false,
                    ["The pre-launch semaphore release failed; launch was not attempted."]);
            }
            else
            {
                var uri = await request.FreshLaunchUri(cancellationToken).ConfigureAwait(false);
                if (!Uri.TryCreate(uri.ToString(), UriKind.Absolute, out var validUri)
                    || (!string.Equals(validUri.Scheme, "roblox-player", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(validUri.Scheme, "roblox", StringComparison.OrdinalIgnoreCase)))
                {
                    verification = new LaunchVerificationResult(
                        LaunchVerificationStatus.Failed,
                        null,
                        false,
                        ["The launch URI was invalid or used an unsupported scheme."]);
                }
                else
                {
                    // Do not log or retain this argument. It can contain an authentication ticket.
                    var command = await _commandRunner.RunAsync(
                        "/usr/bin/open",
                        ["-n", "-a", request.BundlePath, validUri.ToString()],
                        cancellationToken).ConfigureAwait(false);
                    commandSucceeded = command.Succeeded;
                    var timeout = request.VerificationTimeout.GetValueOrDefault(TimeSpan.FromSeconds(30));
                    verification = commandSucceeded
                        ? await _verification.WaitForNewProcessAsync(before, request.BundlePath, timeout, cancellationToken).ConfigureAwait(false)
                        : new LaunchVerificationResult(
                            LaunchVerificationStatus.Failed,
                            null,
                            false,
                            ["The macOS open command failed; no process was assigned to the account."]);
                }
            }
        }
        finally
        {
            // Always remove the semaphore name after the attempted launch, including invalid
            // URI, command failure, cancellation, and exceptions from the URI provider.
            afterUnlink = _semaphore.Unlink();
        }

        if (verification.Succeeded && verification.NewProcess is not null)
        {
            _registry.Register(verification.NewProcess.Identity);
        }
        var warnings = verification.Warnings.ToList();
        if (!beforeUnlink.Succeeded)
        {
            warnings.Add("The pre-launch semaphore release did not complete successfully.");
        }

        if (!afterUnlink.Succeeded)
        {
            warnings.Add("The post-launch semaphore release did not complete successfully.");
        }

        return new MacLaunchResult(
            MacLaunchLevel.OriginalBundle,
            beforeUnlink,
            afterUnlink,
            verification,
            commandSucceeded,
            warnings);
    }
}

/// <summary>Serializes launches and obtains a fresh ticket URI for every retry.</summary>
public sealed class MacLaunchCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MacOriginalBundleLaunchStrategy _strategy;

    public MacLaunchCoordinator(MacOriginalBundleLaunchStrategy strategy)
    {
        _strategy = strategy;
    }

    public async Task<MacLaunchResult> LaunchWithRetriesAsync(
        string bundlePath,
        Func<CancellationToken, ValueTask<Uri>> freshLaunchUri,
        bool userConsented,
        int maxAttempts = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MacLaunchResult? last = null;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                // FreshLaunchUri is intentionally evaluated inside each attempt. Ticket-bearing
                // URIs are never cached or reused after a failed launch.
                last = await _strategy.LaunchAsync(
                    new RobloxLaunchRequest(bundlePath, freshLaunchUri, userConsented),
                    cancellationToken).ConfigureAwait(false);
                if (last.Verification.Succeeded)
                {
                    return last;
                }
            }

            return last!;
        }
        finally
        {
            _gate.Release();
        }
    }
}

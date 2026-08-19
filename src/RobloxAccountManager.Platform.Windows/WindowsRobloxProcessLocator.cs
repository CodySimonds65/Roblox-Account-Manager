using System.ComponentModel;
using System.Diagnostics;
using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.Windows;

public sealed class WindowsRobloxProcessLocator : IRobloxProcessLocator
{
    private readonly Dictionary<string, RobloxProcessInfo> _managed = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;

    public WindowsRobloxProcessLocator(TimeSpan? verificationTimeout = null) =>
        _timeout = verificationTimeout ?? TimeSpan.FromSeconds(45);

    public void RegisterManaged(string accountId, RobloxProcessIdentity identity)
    {
        if (identity.Platform != RobloxPlatform.Windows || !identity.IsValid)
            throw new ArgumentException("A valid Windows process identity is required.", nameof(identity));
        lock (_managed) _managed[accountId] = new RobloxProcessInfo(identity, true, accountId);
    }

    public ValueTask<RobloxLaunchSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, CaptureIdentities()));

    public async ValueTask<LaunchVerificationResult> VerifyLaunchedProcessAsync(
        RobloxLaunchSnapshot before,
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        RobloxProcessIdentity? stable = null;
        var stableCount = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            var current = CaptureIdentities();
            var candidates = current.Where(identity => !before.Contains(identity)).ToArray();
            if (candidates.Length != 1)
            {
                stable = null;
                stableCount = 0;
                continue;
            }

            stableCount = stable?.Matches(candidates[0]) == true ? stableCount + 1 : 1;
            stable = candidates[0];
            if (stableCount < 3) continue;

            var priorDisappeared = before.Processes.Any(prior => !current.Any(now => now.Matches(prior)));
            var result = new RobloxProcessInfo(stable, true, request.AccountId);
            lock (_managed) _managed[request.AccountId] = result;
            return new LaunchVerificationResult(true, result, PriorManagedProcessesDisappeared: priorDisappeared);
        }

        return LaunchVerificationResult.Failure(LaunchFailureKind.ProcessNotFound, "stable-process-not-found");
    }

    public ValueTask<IReadOnlyList<RobloxProcessInfo>> GetManagedProcessesAsync(CancellationToken cancellationToken = default)
    {
        var current = CaptureIdentities();
        lock (_managed)
        {
            foreach (var stale in _managed.Where(pair => !current.Any(identity => identity.Matches(pair.Value.Identity))).Select(pair => pair.Key).ToArray())
                _managed.Remove(stale);
            return ValueTask.FromResult<IReadOnlyList<RobloxProcessInfo>>(_managed.Values.ToArray());
        }
    }

    private static IReadOnlyList<RobloxProcessIdentity> CaptureIdentities()
    {
        var identities = new List<RobloxProcessIdentity>();
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable))
                        identities.Add(new RobloxProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(executable), null, RobloxPlatform.Windows));
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // An unverifiable process can never become managed.
                }
            }
        }
        return identities;
    }
}

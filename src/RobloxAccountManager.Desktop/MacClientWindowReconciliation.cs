using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Desktop;

public sealed record MacClientWindowDuplicate(
    string AccountId,
    IReadOnlyList<int> ProcessIds);

public sealed record MacClientWindowDiscovery(
    IReadOnlyList<RobloxWindowInfo> StableWindows,
    IReadOnlyList<MacClientWindowDuplicate> Duplicates,
    int UnboundProcessCount);

public sealed record MacClientOverlayEligibility(
    IReadOnlyList<RobloxWindowInfo> EligibleWindows,
    IReadOnlyList<MacClientWindowDuplicate> BlockingDuplicates)
{
    public bool CanMutate => EligibleWindows.Count > 0 && BlockingDuplicates.Count == 0;
}

/// <summary>
/// Converts the process-backed macOS client snapshot into one safe window per
/// account. Overlay operations must not guess which of two live processes owns
/// an account's Accessibility window.
/// </summary>
public static class MacClientWindowReconciliation
{
    public static MacClientWindowDiscovery Reconcile(IReadOnlyList<RobloxWindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var stableWindows = new List<RobloxWindowInfo>();
        var duplicates = new List<MacClientWindowDuplicate>();
        var unboundProcessCount = 0;

        foreach (var group in windows
            .GroupBy(window => window.AccountId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                unboundProcessCount += group.Count();
                continue;
            }

            var entries = group
                .OrderBy(window => window.Process.Pid)
                .ToArray();
            if (entries.Length == 1)
            {
                stableWindows.Add(entries[0]);
                continue;
            }

            duplicates.Add(new MacClientWindowDuplicate(
                group.Key,
                entries.Select(window => window.Process.Pid).ToArray()));
        }

        return new MacClientWindowDiscovery(stableWindows, duplicates, unboundProcessCount);
    }

    public static MacClientOverlayEligibility SelectOverlayEligibility(
        MacClientWindowDiscovery discovery,
        Func<string, bool> isOptedIn)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(isOptedIn);

        var eligibleWindows = discovery.StableWindows
            .Where(window => !string.IsNullOrWhiteSpace(window.AccountId)
                && isOptedIn(window.AccountId))
            .ToArray();
        var blockingDuplicates = discovery.Duplicates
            .Where(duplicate => isOptedIn(duplicate.AccountId))
            .ToArray();
        return new MacClientOverlayEligibility(eligibleWindows, blockingDuplicates);
    }
}

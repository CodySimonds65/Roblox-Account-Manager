using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Core.Navigation;

/// <summary>
/// Filters browser custom-scheme navigations without retaining or logging the
/// authentication-ticket URI. A custom scheme is accepted only when a launch is
/// pending and the last committed top-level page is a trusted Roblox origin.
/// </summary>
public sealed class RobloxNavigationGate
{
    private Uri? _lastCommittedTopLevelUri;
    private int _launchPending;

    public bool TryBeginLaunch() => Interlocked.CompareExchange(ref _launchPending, 1, 0) == 0;

    public void CancelPendingLaunch() => Interlocked.Exchange(ref _launchPending, 0);

    /// <summary>Records only a successfully committed top-level navigation.</summary>
    public void CommitTopLevelNavigation(Uri? uri, bool succeeded)
    {
        if (succeeded && uri is { IsAbsoluteUri: true } && uri.Scheme is "https" or "http")
            Volatile.Write(ref _lastCommittedTopLevelUri, uri);
    }

    public BrowserNavigationResult Evaluate(Uri capturedNavigationUri)
    {
        ArgumentNullException.ThrowIfNull(capturedNavigationUri);
        if (!IsTrustedRobloxOrigin(Volatile.Read(ref _lastCommittedTopLevelUri)))
            return BrowserNavigationResult.Rejected("untrusted-top-level-origin");
        if (!IsRobloxScheme(capturedNavigationUri))
            return BrowserNavigationResult.Rejected("unsupported-navigation-scheme");
        if (Interlocked.CompareExchange(ref _launchPending, 0, 1) != 1)
            return BrowserNavigationResult.Rejected("launch-not-pending");

        // The URI is returned only to the immediate launch pipeline. It is never
        // copied into diagnostics, persistent state, or an exception message.
        return new BrowserNavigationResult(true, capturedNavigationUri);
    }

    public static bool IsTrustedRobloxOrigin(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)) &&
        uri.Port is -1 or 443;

    public static bool IsRobloxScheme(Uri uri) =>
        string.Equals(uri.Scheme, "roblox-player", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, "roblox", StringComparison.OrdinalIgnoreCase);
}

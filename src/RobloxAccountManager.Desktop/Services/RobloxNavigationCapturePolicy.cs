using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Navigation;

namespace RobloxAccountManager.Desktop.Services;

/// <summary>
/// Applies the same trusted launch-URI policy to both WebView navigation routes.
/// WKWebView reports some external protocol launches as new-window requests rather
/// than top-level navigation-start events.
/// </summary>
public static class RobloxNavigationCapturePolicy
{
    public static BrowserNavigationResult? Evaluate(
        RobloxNavigationGate gate,
        Uri? request)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (request is null || !RobloxNavigationGate.IsRobloxScheme(request))
        {
            return null;
        }

        return gate.Evaluate(request);
    }
}

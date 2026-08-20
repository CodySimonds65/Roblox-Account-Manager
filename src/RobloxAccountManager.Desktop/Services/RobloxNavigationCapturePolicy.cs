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
        Uri? request,
        string? route = null,
        Action<string>? diagnosticSink = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (request is null || !RobloxNavigationGate.IsRobloxScheme(request))
        {
            return null;
        }

        var result = gate.Evaluate(request);
        if (!string.IsNullOrWhiteSpace(route))
        {
            diagnosticSink?.Invoke(DescribeRoute(route, request, result));
        }

        return result;
    }

    public static string DescribeRoute(
        string route,
        Uri request,
        BrowserNavigationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        var outcome = result.Accepted
            ? "accepted"
            : $"rejected:{SanitiseToken(result.DiagnosticCode)}";
        return $"macos-route: {SanitiseToken(route)} scheme={SanitiseToken(request.Scheme)} outcome={outcome}";
    }

    private static string SanitiseToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : new string(value.Trim().Where(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_').Take(48).ToArray());
}

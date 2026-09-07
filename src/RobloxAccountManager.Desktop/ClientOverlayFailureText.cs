using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Platform.MacOS;

namespace RobloxAccountManager.Desktop;

public static class ClientOverlayFailureText
{
    public static string Describe(string code)
    {
        var parts = code.Split(':');
        var primary = parts[0];
        if (parts.Skip(1).Any(part =>
                string.Equals(part, "restore-overlay-failed", StringComparison.Ordinal)))
        {
            return "Could not verify the original Roblox window state yet; retry restoration from Clients.";
        }
        return primary switch
        {
            "accessibility-permission-required" => "Grant Accessibility permission to place Roblox over the Clients viewport.",
            "accessibility-no-windows" or "accessibility-application-unavailable" => "Waiting for Roblox to publish its game window to Accessibility…",
            "accessibility-no-eligible-window" or "accessibility-window-not-settled" => "Roblox is still publishing a usable window; retry restoration from Clients.",
            "accessibility-window-ambiguous" => "More than one Roblox game-window candidate was found; waiting for a stable main window…",
            "accessible-window-changed" => "Roblox replaced its game window; validating the replacement before placement…",
            "fullscreen-window-not-supported" => "Exit Roblox fullscreen mode before using the Clients panel.",
            "stale-process-identity" => "A Roblox process identity changed; relaunch that account before leaving Clients.",
            "raise-selected-failed" => "The selected client was positioned but macOS could not bring it forward.",
            "raise-cancelled" => "Client placement is ready. Select its tab again to bring Roblox forward.",
            "restore-overlay-failed" => "Could not verify the original Roblox window state yet; retry restoration from Clients.",
            _ when primary.Contains("stale-process-identity", StringComparison.Ordinal) =>
                "A Roblox process identity changed; relaunch that account before leaving Clients.",
            _ when primary.Contains("window-changed", StringComparison.Ordinal) =>
                "Roblox replaced its game window; validating the replacement before placement…",
            _ when primary.StartsWith("hide-unselected-", StringComparison.Ordinal) =>
                "A client could not be minimized safely; every tracked window was restored.",
            _ when primary.Contains("accessibility-frame-size-", StringComparison.Ordinal) =>
                "macOS could not resize the selected Roblox window. See Activity for the Accessibility error.",
            _ when primary.Contains("accessibility-frame-position-", StringComparison.Ordinal) =>
                "macOS could not move the selected Roblox window. See Activity for the Accessibility error.",
            _ when primary.Contains("accessibility-frame-readback-mismatch", StringComparison.Ordinal) =>
                "Roblox constrained the requested Clients viewport size; the original window state was restored.",
            _ when primary.Contains("accessibility-minimized-", StringComparison.Ordinal) =>
                "macOS could not settle the Roblox minimized state. Retry restoration from Clients.",
            _ => $"Client overlay paused: {LaunchDiagnostics.SanitiseCode(code)}"
        };
    }

    public static bool IsRetryable(string code)
    {
        var primary = code.Split(':', 2)[0];
        return primary is "restore-overlay-failed"
            or "accessibility-window-not-settled"
            or "accessibility-no-windows"
            or "accessibility-no-eligible-window"
            || primary.Contains("readback", StringComparison.Ordinal);
    }

    public static bool IsRetryable(MacOverlayOperationResult result)
    {
        var parts = result.DiagnosticCode.Split(':');
        if (parts.Any(part => string.Equals(part, "restore-overlay-failed", StringComparison.Ordinal)))
            return result.Clients.Any(client => client.Retryable);
        return IsRetryable(result.DiagnosticCode)
            || result.Clients.Any(client => client.Retryable);
    }
}

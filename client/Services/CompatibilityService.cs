using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Web.WebView2.Core;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public sealed class CompatibilityService
{
    public Task<IReadOnlyList<CompatibilityCheck>> RunAsync()
    {
        var checks = new List<CompatibilityCheck>
        {
            GetClientVersion(),
            GetWindowsVersion(),
            GetWebView2Status(),
            GetLauncherStatus(),
            GetHandleToolStatus(),
            GetRobloxProcessStatus(),
            GetElevationStatus()
        };

        return Task.FromResult<IReadOnlyList<CompatibilityCheck>>(checks);
    }

    public static string CreateSafeReport(IEnumerable<CompatibilityCheck> checks)
    {
        var lines = new List<string>
        {
            "Roblox Account Manager diagnostics",
            $"Generated: {DateTimeOffset.Now:O}",
            string.Empty
        };

        lines.AddRange(checks.Select(check =>
            $"[{check.StateLabel}] {check.Name}: {check.Summary} — {check.Detail}"));
        lines.Add(string.Empty);
        lines.Add("This report excludes account labels, URLs, cookies, tokens, and local file paths.");
        return string.Join(Environment.NewLine, lines);
    }

    private static CompatibilityCheck GetClientVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        return new CompatibilityCheck("Client", CompatibilityCheckState.Ready, $"Version {version}", "Automatic update checks enabled");
    }

    private static CompatibilityCheck GetWindowsVersion() =>
        new("Windows", CompatibilityCheckState.Info, Environment.OSVersion.VersionString, Environment.Is64BitOperatingSystem ? "64-bit operating system" : "32-bit operating system");

    private static CompatibilityCheck GetWebView2Status()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return new CompatibilityCheck("WebView2", CompatibilityCheckState.Ready, "Installed", $"Runtime {version}");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return new CompatibilityCheck("WebView2", CompatibilityCheckState.Warning, "Missing", "Install Microsoft WebView2 Runtime");
        }
    }

    private static CompatibilityCheck GetLauncherStatus()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bloxstrap = Path.Combine(localAppData, "Bloxstrap", "Bloxstrap.exe");
        var standardRoot = Path.Combine(localAppData, "Roblox", "Versions");
        var standardInstalled = Directory.Exists(standardRoot) &&
                                Directory.EnumerateDirectories(standardRoot)
                                    .Any(directory => File.Exists(Path.Combine(directory, "RobloxPlayerBeta.exe")));

        if (File.Exists(bloxstrap) && standardInstalled)
        {
            return new CompatibilityCheck("Roblox launcher", CompatibilityCheckState.Ready, "Bloxstrap and standard Roblox detected", "Windows chooses the registered roblox-player handler");
        }

        if (File.Exists(bloxstrap))
        {
            return new CompatibilityCheck("Roblox launcher", CompatibilityCheckState.Ready, "Bloxstrap detected", "Compatible through the roblox-player protocol");
        }

        if (standardInstalled)
        {
            return new CompatibilityCheck("Roblox launcher", CompatibilityCheckState.Ready, "Standard Roblox detected", "RobloxPlayerBeta is installed");
        }

        return new CompatibilityCheck("Roblox launcher", CompatibilityCheckState.Warning, "Not detected", "Install Roblox or Bloxstrap before launching");
    }

    private static CompatibilityCheck GetHandleToolStatus()
    {
        var handlePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient",
            "Tools",
            "handle64.exe");

        return File.Exists(handlePath)
            ? new CompatibilityCheck("Sysinternals Handle", CompatibilityCheckState.Ready, "Cached", "Ready for multi-client singleton release")
            : new CompatibilityCheck("Sysinternals Handle", CompatibilityCheckState.Info, "Downloads on first use", "Retrieved directly from Microsoft");
    }

    private static CompatibilityCheck GetRobloxProcessStatus()
    {
        var processCount = Process.GetProcessesByName("RobloxPlayerBeta").Length;
        return new CompatibilityCheck(
            "Roblox processes",
            CompatibilityCheckState.Info,
            processCount == 0 ? "None running" : $"{processCount} running",
            processCount == 0 ? "The next launch will be the first client" : "Additional clients require singleton release");
    }

    private static CompatibilityCheck GetElevationStatus()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return new CompatibilityCheck(
            "Administrator access",
            CompatibilityCheckState.Info,
            elevated ? "Elevated at startup" : "Not elevated",
            "The client requests administrator access once at startup and reuses that context for singleton release");
    }
}

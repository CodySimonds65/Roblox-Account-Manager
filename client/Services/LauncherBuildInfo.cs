using System.Reflection;

namespace RobloxAltClient.Services;

internal static class LauncherBuildInfo
{
    private static readonly Assembly Assembly = typeof(LauncherBuildInfo).Assembly;
    public static string? DiagnosticLabel => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "DiagnosticBuildLabel")?.Value;

    public static string ActivityHeader =>
        $"Roblox Account Manager {Assembly.GetName().Version?.ToString(3) ?? "unknown"}; " +
        $"build: {DiagnosticLabel ?? "standard"}; startup check: {RobloxStartupTracker<int>.StartupInterval.TotalSeconds:0} seconds.";
}

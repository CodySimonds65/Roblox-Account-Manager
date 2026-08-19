namespace RobloxAccountManager.Desktop;

internal static class TrustedRobloxIdentityConfiguration
{
    private const string EnvironmentVariable = "RAM_TRUSTED_ROBLOX_TEAM_ID";
    private const string InstallerEnvironmentVariable = "RAM_TRUSTED_INSTALLER_IDENTITY";
    private const string ResourceFileName = "RobloxDeveloperTeamIdentifier";
    private const string InstallerResourceFileName = "RobloxInstallerIdentity";

    public static string? LoadTeamIdentifier()
    {
        // Packaged builds place this inside the signed application bundle. Prefer it over
        // ambient process state so the release identity cannot be silently overridden.
        var resourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Resources",
            ResourceFileName));
        if (File.Exists(resourcePath))
        {
            var packaged = File.ReadAllText(resourcePath).Trim();
            return string.IsNullOrWhiteSpace(packaged) ? null : packaged;
        }

        var development = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        return string.IsNullOrWhiteSpace(development) ? null : development;
    }

    public static string? LoadInstallerIdentity()
    {
        var resourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Resources",
            InstallerResourceFileName));
        if (File.Exists(resourcePath))
        {
            var packaged = File.ReadAllText(resourcePath).Trim();
            return string.IsNullOrWhiteSpace(packaged) ? null : packaged;
        }

        var development = Environment.GetEnvironmentVariable(InstallerEnvironmentVariable)?.Trim();
        return string.IsNullOrWhiteSpace(development) ? null : development;
    }
}

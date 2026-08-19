namespace RobloxAccountManager.Desktop;

internal static class TrustedRobloxIdentityConfiguration
{
    private const string InstallerEnvironmentVariable = "RAM_TRUSTED_INSTALLER_IDENTITY";
    private const string InstallerResourceFileName = "RobloxInstallerIdentity";

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

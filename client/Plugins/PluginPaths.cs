namespace RobloxAltClient.Plugins;

public sealed class PluginPaths
{
    public string Root { get; }
    public string InstallRoot => Path.Combine(Root, "Plugins");
    public string DataRoot => Path.Combine(Root, "PluginData");
    public string CatalogPath => Path.Combine(Root, "plugin-catalog.json");
    public string ConsentPath => Path.Combine(Root, "plugin-consent.json");

    public PluginPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(InstallRoot);
        Directory.CreateDirectory(DataRoot);
    }

    public string GetInstallDirectory(string pluginId)
    {
        ValidatePluginId(pluginId);
        return Path.Combine(InstallRoot, pluginId);
    }

    public string GetDataDirectory(string pluginId)
    {
        ValidatePluginId(pluginId);
        var path = Path.Combine(DataRoot, pluginId);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void ValidatePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 200 ||
            pluginId.Split('.').Any(part => part.Length == 0 || part.Any(ch => !(char.IsLower(ch) || char.IsDigit(ch) || ch == '-'))))
            throw new ArgumentException("Invalid plugin id.", nameof(pluginId));
    }
}

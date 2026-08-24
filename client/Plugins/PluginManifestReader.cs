using RobloxAccountManager.PluginSdk;

namespace RobloxAltClient.Plugins;

public static class PluginManifestReader
{
    public static PluginManifest Parse(string json, string? runtimeIdentifier = null) =>
        PluginManifestParser.Parse(json, runtimeIdentifier);

    public static void ValidateRelativePath(string value, string fieldName) =>
        PluginManifestParser.ValidateRelativePath(value, fieldName);
}

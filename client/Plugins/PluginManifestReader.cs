using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxAltClient.Plugins;

public static class PluginManifestReader
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled);
    private static readonly Regex CapabilityPattern = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled);

    public static PluginManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 16,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var root = document.RootElement;
        var schema = RequiredInt(root, "schemaVersion");
        if (schema != 1)
        {
            throw new InvalidDataException($"Unsupported plugin schemaVersion {schema}.");
        }

        var id = RequiredString(root, "id");
        if (!IdPattern.IsMatch(id))
        {
            throw new InvalidDataException("Plugin id must use reverse-DNS lowercase form.");
        }

        var capabilities = root.TryGetProperty("capabilities", out var capabilityElement) &&
                           capabilityElement.ValueKind == JsonValueKind.Array
            ? capabilityElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray()
            : throw new InvalidDataException("Plugin capabilities are required.");
        if (capabilities.Any(value => value.Length > 128 || !CapabilityPattern.IsMatch(value)))
            throw new InvalidDataException("Plugin capabilities must use lowercase reverse-DNS names.");

        var entryPoint = root.TryGetProperty("entryPoint", out var entryPointElement)
            ? entryPointElement.GetString()
            : null;
        entryPoint = string.IsNullOrWhiteSpace(entryPoint) ? id + ".exe" : entryPoint;
        ValidateRelativePath(entryPoint, "entryPoint");
        var icon = OptionalString(root, "icon");
        if (icon is not null) ValidateRelativePath(icon, "icon");
        var updateFeed = OptionalString(root, "updateFeed");
        if (updateFeed is not null && (!Uri.TryCreate(updateFeed, UriKind.Absolute, out var updateUri) || updateUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("Plugin updateFeed must be an https URL.");

        return new PluginManifest(
            schema,
            id,
            RequiredString(root, "name"),
            RequiredString(root, "version"),
            RequiredString(root, "contractVersion"),
            RequiredString(root, "publisher"),
            RequiredString(root, "description"),
            capabilities,
            entryPoint,
            icon,
            updateFeed,
            OptionalString(root, "minHostVersion"),
            root.TryGetProperty("autostartDefault", out var autostart) && autostart.ValueKind == JsonValueKind.True);
    }

    public static void ValidateRelativePath(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Contains(':') || value.Contains('\\') || value.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Plugin {fieldName} must be a safe relative path.");
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new InvalidDataException($"Plugin manifest is missing {name}.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"Plugin manifest is missing {name}.");
}

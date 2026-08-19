using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

public static class PluginManifestReader
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled);
    private static readonly Regex CapabilityPattern = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedRuntimeIdentifiers =
        ["win-x64", "osx-arm64", "osx-x64"];

    public static PluginManifest Parse(string json, string? runtimeIdentifier = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            MaxDepth = 16,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var root = document.RootElement;
        var schema = RequiredInt(root, "schemaVersion");
        if (schema is not (1 or 2))
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

        runtimeIdentifier ??= CurrentRuntimeIdentifier();
        var entryPoints = schema == 1
            ? ParseLegacyEntryPoint(root, id)
            : ParseRidEntryPoints(root);
        var entryPoint = entryPoints.TryGetValue(runtimeIdentifier, out var selectedEntryPoint)
            ? selectedEntryPoint
            : string.Empty;
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
            root.TryGetProperty("autostartDefault", out var autostart) && autostart.ValueKind == JsonValueKind.True,
            entryPoints,
            runtimeIdentifier);
    }

    private static IReadOnlyDictionary<string, string> ParseLegacyEntryPoint(JsonElement root, string id)
    {
        var entryPoint = root.TryGetProperty("entryPoint", out var element) ? element.GetString() : null;
        entryPoint = string.IsNullOrWhiteSpace(entryPoint) ? id + ".exe" : entryPoint;
        ValidateRelativePath(entryPoint, "entryPoint");
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["win-x64"] = entryPoint };
    }

    private static IReadOnlyDictionary<string, string> ParseRidEntryPoints(JsonElement root)
    {
        if (!root.TryGetProperty("entryPoints", out var element) || element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Plugin schema 2 requires an entryPoints object.");

        var entryPoints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!SupportedRuntimeIdentifiers.Contains(property.Name))
                throw new InvalidDataException($"Plugin entryPoints contains unsupported RID '{property.Name}'.");
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                throw new InvalidDataException($"Plugin entryPoints value for '{property.Name}' is invalid.");
            var path = property.Value.GetString()!;
            ValidateRelativePath(path, $"entryPoints.{property.Name}");
            if (!entryPoints.TryAdd(property.Name, path))
                throw new InvalidDataException($"Plugin entryPoints contains duplicate RID '{property.Name}'.");
        }

        if (entryPoints.Count == 0)
            throw new InvalidDataException("Plugin schema 2 requires at least one RID entrypoint.");
        return entryPoints;
    }

    private static string CurrentRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsMacOS()) return "osx-" + architecture;
        if (OperatingSystem.IsWindows()) return "win-" + architecture;
        return RuntimeInformation.RuntimeIdentifier;
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

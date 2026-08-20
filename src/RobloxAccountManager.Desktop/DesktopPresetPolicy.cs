using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Desktop;

public static class DesktopPresetPolicy
{
    public const string CustomUrlPresetName = "Custom URL";

    public static IReadOnlyList<GamePreset> FilterPresets(
        IEnumerable<GamePreset> presets,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var trimmedQuery = query?.Trim() ?? string.Empty;
        return presets
            .Where(preset => trimmedQuery.Length == 0 || preset.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static bool IsCustomUrlPreset(GamePreset? preset) =>
        preset is not null &&
        preset.IsBuiltIn &&
        string.Equals(preset.Name, CustomUrlPresetName, StringComparison.OrdinalIgnoreCase);

    public static string GetUrlEditorValue(GamePreset? preset) =>
        IsCustomUrlPreset(preset) ? string.Empty : preset?.Url ?? string.Empty;

    public static bool TryResolveLaunchUrl(
        GamePreset? preset,
        string? editorValue,
        out string normalizedUrl)
    {
        var candidate = IsCustomUrlPreset(preset) ? editorValue : preset?.Url;
        return GamePreset.TryNormalizeRobloxGameUrl(candidate ?? string.Empty, out normalizedUrl);
    }
}

namespace RobloxAltClient.Models;

/// <summary>
/// Roblox menu preferences and engine overrides used by the launcher. Nullable
/// values are used for game and profile overrides so each scope can inherit
/// lower-priority values.
/// </summary>
public sealed class GameSettings
{
    public int? MsaaSamples { get; set; }
    public bool? PreserveRenderingQuality { get; set; }
    public int? GraphicsQuality { get; set; }
    public int? TextureQuality { get; set; }
    public int? FpsLimit { get; set; }
    public int? MasterVolumeLevel { get; set; }
    public string? AdvancedFlagsJson { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasOverrides =>
        MsaaSamples.HasValue ||
        PreserveRenderingQuality.HasValue ||
        GraphicsQuality.HasValue ||
        TextureQuality.HasValue ||
        FpsLimit.HasValue ||
        MasterVolumeLevel.HasValue ||
        HasAdvancedOverrides();

    public GameSettings Clone() => new()
    {
        MsaaSamples = MsaaSamples,
        PreserveRenderingQuality = PreserveRenderingQuality,
        GraphicsQuality = GraphicsQuality,
        TextureQuality = TextureQuality,
        FpsLimit = FpsLimit,
        MasterVolumeLevel = MasterVolumeLevel,
        AdvancedFlagsJson = AdvancedFlagsJson
    };

    public static GameSettings Merge(GameSettings global, GameSettings? overrideSettings)
        => Resolve(global, overrideSettings, null);

    public static bool TryResolve(
        GameSettings global,
        GameSettings? gameOverride,
        GameSettings? profileOverride,
        out GameSettings resolved,
        out string error)
    {
        foreach (var (scope, settings) in new[]
                 {
                     ("Global", global),
                     ("Game", gameOverride),
                     ("Profile", profileOverride)
                 })
        {
            if (settings is not null && !Services.RobloxClientSettingsService.TryValidateSettings(settings, out var scopeError))
            {
                resolved = new GameSettings();
                error = $"{scope} settings are invalid: {scopeError}";
                return false;
            }
        }

        resolved = ResolveUnchecked(global, gameOverride, profileOverride);
        error = string.Empty;
        return true;
    }

    public static GameSettings Resolve(
        GameSettings global,
        GameSettings? gameOverride,
        GameSettings? profileOverride)
    {
        if (!TryResolve(global, gameOverride, profileOverride, out var resolved, out var error))
        {
            throw new ArgumentException(error, nameof(global));
        }

        return resolved;
    }

    private static GameSettings ResolveUnchecked(
        GameSettings global,
        GameSettings? gameOverride,
        GameSettings? profileOverride)
    {
        var merged = global.Clone();
        ApplyOverride(merged, gameOverride);
        ApplyOverride(merged, profileOverride);
        merged.AdvancedFlagsJson = MergeAdvancedJson(
            global.AdvancedFlagsJson,
            gameOverride?.AdvancedFlagsJson,
            profileOverride?.AdvancedFlagsJson);
        return merged;
    }

    private static void ApplyOverride(GameSettings merged, GameSettings? overrideSettings)
    {
        if (overrideSettings is null)
        {
            return;
        }

        if (overrideSettings.MsaaSamples.HasValue)
        {
            merged.MsaaSamples = overrideSettings.MsaaSamples;
        }

        if (overrideSettings.PreserveRenderingQuality.HasValue)
        {
            merged.PreserveRenderingQuality = overrideSettings.PreserveRenderingQuality;
        }

        if (overrideSettings.TextureQuality.HasValue)
        {
            merged.TextureQuality = overrideSettings.TextureQuality;
        }

        if (overrideSettings.GraphicsQuality.HasValue)
        {
            merged.GraphicsQuality = overrideSettings.GraphicsQuality;
        }

        if (overrideSettings.FpsLimit.HasValue)
        {
            merged.FpsLimit = overrideSettings.FpsLimit;
        }

        if (overrideSettings.MasterVolumeLevel.HasValue)
        {
            merged.MasterVolumeLevel = overrideSettings.MasterVolumeLevel;
        }
    }

    private static string? MergeAdvancedJson(params string?[] jsonLayers)
    {
        var values = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in jsonLayers)
        {
            if (!Services.RobloxClientSettingsService.TryParseAdvancedFlags(json, out var flags, out _))
            {
                continue;
            }

            foreach (var pair in flags)
            {
                if (pair.Value.ValueKind == System.Text.Json.JsonValueKind.Null)
                {
                    values.Remove(pair.Key);
                }
                else
                {
                    values[pair.Key] = pair.Value;
                }
            }
        }

        if (values.Count == 0)
        {
            return jsonLayers.Any(json => !string.IsNullOrWhiteSpace(json)) ? "{}" : null;
        }

        return System.Text.Json.JsonSerializer.Serialize(values, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private bool HasAdvancedOverrides()
    {
        if (string.IsNullOrWhiteSpace(AdvancedFlagsJson))
        {
            return false;
        }

        return !Services.RobloxClientSettingsService.TryParseAdvancedFlags(AdvancedFlagsJson, out var flags, out _) ||
               flags.Count > 0;
    }
}

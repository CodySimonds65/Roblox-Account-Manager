namespace RobloxAltClient.Models;

/// <summary>
/// Roblox menu preferences and engine overrides used by the launcher. Nullable
/// values are used for per-game settings so a game can inherit the global value.
/// </summary>
public sealed class GameSettings
{
    public int? MsaaSamples { get; set; }
    public bool? PreserveRenderingQuality { get; set; }
    public int? GraphicsQuality { get; set; }
    public int? TextureQuality { get; set; }
    public int? FpsLimit { get; set; }
    public string? AdvancedFlagsJson { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasOverrides =>
        MsaaSamples.HasValue ||
        PreserveRenderingQuality.HasValue ||
        GraphicsQuality.HasValue ||
        TextureQuality.HasValue ||
        FpsLimit.HasValue ||
        HasAdvancedOverrides();

    public GameSettings Clone() => new()
    {
        MsaaSamples = MsaaSamples,
        PreserveRenderingQuality = PreserveRenderingQuality,
        GraphicsQuality = GraphicsQuality,
        TextureQuality = TextureQuality,
        FpsLimit = FpsLimit,
        AdvancedFlagsJson = AdvancedFlagsJson
    };

    public static GameSettings Merge(GameSettings global, GameSettings? overrideSettings)
    {
        var merged = global.Clone();
        if (overrideSettings is null)
        {
            return merged;
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

        if (!string.IsNullOrWhiteSpace(overrideSettings.AdvancedFlagsJson))
        {
            merged.AdvancedFlagsJson = MergeAdvancedJson(global.AdvancedFlagsJson, overrideSettings.AdvancedFlagsJson);
        }

        return merged;
    }

    private static string MergeAdvancedJson(string? globalJson, string? overrideJson)
    {
        var values = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (Services.RobloxClientSettingsService.TryParseAdvancedFlags(globalJson, out var globalFlags, out _))
        {
            foreach (var pair in globalFlags)
            {
                values[pair.Key] = pair.Value;
            }
        }

        if (Services.RobloxClientSettingsService.TryParseAdvancedFlags(overrideJson, out var overrideFlags, out _))
        {
            foreach (var pair in overrideFlags)
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

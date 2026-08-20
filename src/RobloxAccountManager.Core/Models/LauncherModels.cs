using System.Text.Json;

namespace RobloxAccountManager.Core.Models;

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Roblox account";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Group { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public bool EmbedInClients { get; set; }
    public int SortOrder { get; set; }
    public GameSettings? GameSettings { get; set; }

    public override string ToString() => Label;
}

public sealed record GamePreset(string Name, string Url, bool IsBuiltIn = false)
{
    public GameSettings? Settings { get; set; }

    public override string ToString() => Name;

    public static bool TryNormalizeRobloxGameUrl(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !string.Equals(segments[0], "games", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(segments[1], out _))
        {
            return false;
        }

        if (string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase))
        {
            var canonical = new UriBuilder(uri) { Host = "www.roblox.com" };
            normalizedUrl = canonical.Uri.AbsoluteUri;
        }
        else
        {
            normalizedUrl = uri.AbsoluteUri;
        }
        return true;
    }
}

public sealed class LauncherSettings
{
    public bool UpdateChecksEnabled { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Signed;
    public int LaunchTimeoutSeconds { get; set; } = 45;
    public int LaunchDelaySeconds { get; set; }
    public bool ContinueOnFailure { get; set; } = true;
    public bool RememberSelections { get; set; } = true;
    public string PreferredLauncher { get; set; } = "Auto";
    public List<string> LastSelectedProfileIds { get; set; } = [];
    public string LastGameName { get; set; } = string.Empty;
    public List<string> RecentGameNames { get; set; } = [];
    public bool ClearBrowserDataOnNextStart { get; set; }
    public bool MultiInstanceConsentGranted { get; set; }
    public bool RobloxSettingsConsentGranted { get; set; }
    public bool UnsignedUpdatesConsentGranted { get; set; }
    public GameSettings GameSettings { get; set; } = new();
    public Dictionary<string, GameSettings> GameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum UpdateChannel
{
    Signed,
    Unsigned
}

public sealed class GameSettings
{
    public int? MsaaSamples { get; set; }
    public bool? PreserveRenderingQuality { get; set; }
    public int? GraphicsQuality { get; set; }
    public int? TextureQuality { get; set; }
    public int? FpsLimit { get; set; }
    public int? MasterVolumeLevel { get; set; }
    public string? AdvancedFlagsJson { get; set; }

    public bool HasOverrides =>
        MsaaSamples.HasValue || PreserveRenderingQuality.HasValue || GraphicsQuality.HasValue ||
        TextureQuality.HasValue || FpsLimit.HasValue || MasterVolumeLevel.HasValue || HasAdvancedOverrides();

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

    public static GameSettings Resolve(GameSettings global, GameSettings? game, GameSettings? profile)
    {
        ArgumentNullException.ThrowIfNull(global);
        Validate(global, "Global");
        if (game is not null) Validate(game, "Game");
        if (profile is not null) Validate(profile, "Profile");

        var merged = global.Clone();
        Apply(merged, game);
        Apply(merged, profile);
        merged.AdvancedFlagsJson = MergeAdvancedJson(global.AdvancedFlagsJson, game?.AdvancedFlagsJson, profile?.AdvancedFlagsJson);
        return merged;
    }

    public static bool TryValidate(GameSettings settings, out string error)
    {
        try
        {
            Validate(settings, "Settings");
            error = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void Validate(GameSettings settings, string scope)
    {
        if (settings.MsaaSamples is not null && settings.MsaaSamples is not (0 or 2 or 4 or 8))
            throw new ArgumentException($"{scope} MSAA must be 0, 2, 4, or 8.");
        if (settings.GraphicsQuality is < 1 or > 10)
            throw new ArgumentException($"{scope} graphics quality must be between 1 and 10.");
        if (settings.TextureQuality is < 0 or > 6)
            throw new ArgumentException($"{scope} texture quality must be between 0 and 6.");
        if (settings.FpsLimit is < 30 or > 1000)
            throw new ArgumentException($"{scope} FPS must be between 30 and 1000.");
        if (settings.MasterVolumeLevel is < 0 or > 10)
            throw new ArgumentException($"{scope} volume must be between 0 and 10.");
        if (!string.IsNullOrWhiteSpace(settings.AdvancedFlagsJson) &&
            !TryParseAdvancedFlags(settings.AdvancedFlagsJson, out _, out var error))
            throw new ArgumentException($"{scope} advanced flags are invalid: {error}");
    }

    private static void Apply(GameSettings target, GameSettings? source)
    {
        if (source is null) return;
        target.MsaaSamples = source.MsaaSamples ?? target.MsaaSamples;
        target.PreserveRenderingQuality = source.PreserveRenderingQuality ?? target.PreserveRenderingQuality;
        target.GraphicsQuality = source.GraphicsQuality ?? target.GraphicsQuality;
        target.TextureQuality = source.TextureQuality ?? target.TextureQuality;
        target.FpsLimit = source.FpsLimit ?? target.FpsLimit;
        target.MasterVolumeLevel = source.MasterVolumeLevel ?? target.MasterVolumeLevel;
    }

    public static bool TryParseAdvancedFlags(string? json, out Dictionary<string, JsonElement> flags, out string error)
    {
        flags = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "the value must be a JSON object";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
                flags[property.Name] = property.Value.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string? MergeAdvancedJson(params string?[] layers)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            if (!TryParseAdvancedFlags(layer, out var flags, out _)) continue;
            foreach (var pair in flags)
            {
                if (pair.Value.ValueKind == JsonValueKind.Null) values.Remove(pair.Key);
                else values[pair.Key] = pair.Value;
            }
        }
        return values.Count == 0 ? null : JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
    }

    private bool HasAdvancedOverrides() => TryParseAdvancedFlags(AdvancedFlagsJson, out var flags, out _) && flags.Count > 0;
}

public enum LaunchQueueState { Waiting, Preparing, Launching, Running, Failed, Canceled }

public sealed class LaunchQueueItem(AccountProfile account)
{
    public AccountProfile Account { get; } = account;
    public string Label => Account.Label;
    public LaunchQueueState State { get; set; } = LaunchQueueState.Waiting;
    public string Detail { get; set; } = "Waiting";
}

using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Core.Launch;

/// <summary>
/// Resolves the settings applied to one launch using the shared precedence rules:
/// global, preset, URL override, then account.
/// </summary>
public static class LaunchSettingsResolver
{
    public static bool TryResolve(
        LauncherSettings launcherSettings,
        GamePreset? preset,
        string gameUrl,
        AccountProfile account,
        out GameSettings resolved,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(launcherSettings);
        ArgumentNullException.ThrowIfNull(account);

        if (!GamePreset.TryNormalizeRobloxGameUrl(gameUrl, out var normalizedUrl))
        {
            resolved = new GameSettings();
            error = "The launch URL is not a valid Roblox game or private-server URL.";
            return false;
        }

        var globalSettings = launcherSettings.GameSettings ?? new GameSettings();
        var gameOverrides = launcherSettings.GameOverrides
                            ?? new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase);
        gameOverrides.TryGetValue(normalizedUrl, out var urlOverride);

        if (!GameSettings.TryResolve(
                new GameSettings(),
                preset?.Settings,
                urlOverride,
                out var gameSettings,
                out var gameError))
        {
            resolved = new GameSettings();
            error = $"Game settings are invalid: {gameError}";
            return false;
        }

        return GameSettings.TryResolve(
            globalSettings,
            gameSettings.HasOverrides ? gameSettings : null,
            account.GameSettings,
            out resolved,
            out error);
    }
}

using System.Text.Json;
using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Core.Data;

public sealed class LauncherDataPaths
{
    public LauncherDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAltClient");
    }

    public string Root { get; }
    public string Accounts => Path.Combine(Root, "accounts.json");
    public string Presets => Path.Combine(Root, "game-presets.json");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Browser => Path.Combine(Root, "WebView2");
}

public sealed class AccountStore(LauncherDataPaths? paths = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public LauncherDataPaths Paths { get; } = paths ?? new LauncherDataPaths();

    public async Task<List<AccountProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await JsonFileStore.LoadAsync(Paths.Accounts, new List<AccountProfile>(), Options, cancellationToken);
        return OrderForDisplay(accounts);
    }

    public Task SaveAsync(IEnumerable<AccountProfile> accounts, CancellationToken cancellationToken = default) =>
        JsonFileStore.SaveAsync(Paths.Accounts, accounts.ToList(), Options, cancellationToken);

    public static List<AccountProfile> OrderForDisplay(IEnumerable<AccountProfile> accounts) => accounts
        .OrderByDescending(x => x.IsFavorite).ThenBy(x => x.SortOrder).ThenBy(x => x.CreatedUtc).ToList();
}

public sealed class GamePresetStore(LauncherDataPaths? paths = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public LauncherDataPaths Paths { get; } = paths ?? new LauncherDataPaths();
    public Task<List<GamePreset>> LoadAsync(CancellationToken cancellationToken = default) =>
        JsonFileStore.LoadAsync(Paths.Presets, new List<GamePreset>(), Options, cancellationToken);
    public Task SaveAsync(IEnumerable<GamePreset> presets, CancellationToken cancellationToken = default) =>
        JsonFileStore.SaveAsync(Paths.Presets, presets.Where(preset => !preset.IsBuiltIn).ToList(), Options, cancellationToken);

    public static IReadOnlyList<GamePreset> EnsureBuiltIns(IEnumerable<GamePreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var builtIns = new[]
        {
            new GamePreset("Dungeon Quest Reborn", "https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn", true),
            new GamePreset("Custom URL", "https://www.roblox.com/games/", true)
        };
        var result = new List<GamePreset>();
        foreach (var preset in presets.Where(item => !item.IsBuiltIn))
        {
            if (builtIns.Any(builtIn => SameUrl(preset.Url, builtIn.Url)) ||
                result.Any(existing => string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase) || SameUrl(existing.Url, preset.Url))) continue;
            result.Add(preset);
        }
        result.InsertRange(0, builtIns);
        return result;
    }

    private static bool SameUrl(string left, string right)
    {
        if (string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return GamePreset.TryNormalizeRobloxGameUrl(left, out var normalizedLeft)
            && GamePreset.TryNormalizeRobloxGameUrl(right, out var normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SettingsStore(LauncherDataPaths? paths = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public LauncherDataPaths Paths { get; } = paths ?? new LauncherDataPaths();
    public Task<LauncherSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        JsonFileStore.LoadAsync(Paths.Settings, new LauncherSettings(), Options, cancellationToken);
    public Task SaveAsync(LauncherSettings settings, CancellationToken cancellationToken = default) =>
        JsonFileStore.SaveAsync(Paths.Settings, settings, Options, cancellationToken);
}

public static class PresetTransferService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task ExportAsync(string path, IEnumerable<GamePreset> presets, CancellationToken cancellationToken = default)
    {
        var export = presets.Where(x => !x.IsBuiltIn).Select(x => new GamePreset(x.Name, x.Url) { Settings = x.Settings?.Clone() }).ToList();
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, export, Options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<GamePreset>> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imported = await JsonSerializer.DeserializeAsync<List<GamePreset>>(stream, Options, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("The preset file is empty.");
        if (imported.Count > 200) throw new InvalidOperationException("A preset file may contain at most 200 games.");
        var result = new List<GamePreset>();
        foreach (var preset in imported)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > 60 ||
                !GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out var url))
                throw new InvalidOperationException("The preset file contains an invalid game name or Roblox URL.");
            if (result.Any(x => string.Equals(x.Name, preset.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase))) continue;
            if (preset.Settings is not null && !GameSettings.TryValidate(preset.Settings, out var error))
                throw new InvalidOperationException($"The preset file contains invalid settings: {error}");
            result.Add(new GamePreset(preset.Name.Trim(), url) { Settings = preset.Settings?.Clone() });
        }
        return result;
    }
}

public sealed record ProfileTransferPackage(
    IReadOnlyList<AccountProfile> Accounts,
    IReadOnlyList<GamePreset> Presets,
    LauncherSettings Settings,
    string FormatVersion = "1");

public static class ProfileTransferService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task ExportAsync(string path, IEnumerable<AccountProfile> accounts, IEnumerable<GamePreset> presets, LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        var package = new ProfileTransferPackage(
            accounts.Select(CloneAccount).ToList(),
            presets.Where(x => !x.IsBuiltIn).Select(x => new GamePreset(x.Name, x.Url) { Settings = x.Settings?.Clone() }).ToList(),
            CloneSettingsForTransfer(settings),
            "1");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, package, Options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ProfileTransferPackage> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var package = await JsonSerializer.DeserializeAsync<ProfileTransferPackage>(stream, Options, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("The profile package is empty.");
        if (package.Accounts is null || package.Presets is null || package.Settings is null
            || package.Settings.GameSettings is null || package.Settings.GameOverrides is null
            || package.Settings.LastSelectedProfileIds is null || package.Settings.RecentGameNames is null)
            throw new InvalidOperationException("The profile package is missing required sections.");
        if (package.Accounts.Count > 200 || package.Presets.Count > 200)
            throw new InvalidOperationException("A profile package may contain at most 200 accounts and 200 presets.");
        foreach (var account in package.Accounts)
        {
            if (!Guid.TryParse(account.Id, out _)) throw new InvalidOperationException("The profile package contains an invalid account id.");
            if (string.IsNullOrWhiteSpace(account.Label) || account.Label.Length > 120) throw new InvalidOperationException("The profile package contains an invalid account label.");
            if (account.GameSettings is not null && !GameSettings.TryValidate(account.GameSettings, out var accountSettingsError))
                throw new InvalidOperationException($"The profile package contains invalid account settings: {accountSettingsError}");
        }
        foreach (var preset in package.Presets)
        {
            if (!GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out _)) throw new InvalidOperationException("The profile package contains an invalid Roblox URL.");
            if (preset.Settings is not null && !GameSettings.TryValidate(preset.Settings, out var presetSettingsError))
                throw new InvalidOperationException($"The profile package contains invalid preset settings: {presetSettingsError}");
        }

        if (!GameSettings.TryValidate(package.Settings.GameSettings, out var globalSettingsError))
            throw new InvalidOperationException($"The profile package contains invalid global settings: {globalSettingsError}");
        foreach (var overrideSettings in package.Settings.GameOverrides.Values)
            if (!GameSettings.TryValidate(overrideSettings, out var overrideError))
                throw new InvalidOperationException($"The profile package contains invalid scoped settings: {overrideError}");

        // Consent is intentionally local to the current installation and user. Never import a
        // decision that changes Roblox runtime state, deletes browser data, or installs unsigned code.
        return package with { Settings = CloneSettingsForTransfer(package.Settings) };
    }

    private static AccountProfile CloneAccount(AccountProfile account) => new()
    {
        Id = account.Id,
        Label = account.Label,
        CreatedUtc = account.CreatedUtc,
        Group = account.Group,
        IsFavorite = account.IsFavorite,
        EmbedInClients = account.EmbedInClients,
        SortOrder = account.SortOrder,
        GameSettings = account.GameSettings?.Clone()
    };

    private static LauncherSettings CloneSettingsForTransfer(LauncherSettings settings) => new()
    {
        UpdateChecksEnabled = settings.UpdateChecksEnabled,
        UpdateChannel = settings.UpdateChannel,
        LaunchTimeoutSeconds = settings.LaunchTimeoutSeconds,
        LaunchDelaySeconds = settings.LaunchDelaySeconds,
        ContinueOnFailure = settings.ContinueOnFailure,
        RememberSelections = settings.RememberSelections,
        PreferredLauncher = settings.PreferredLauncher,
        LastSelectedProfileIds = settings.LastSelectedProfileIds.ToList(),
        LastGameName = settings.LastGameName,
        RecentGameNames = settings.RecentGameNames.ToList(),
        // ClearBrowserDataOnNextStart, MultiInstanceConsentGranted, and
        // UnsignedUpdatesConsentGranted are deliberately reset for imported data.
        ClearBrowserDataOnNextStart = false,
        MultiInstanceConsentGranted = false,
        RobloxSettingsConsentGranted = false,
        UnsignedUpdatesConsentGranted = false,
        GameSettings = settings.GameSettings?.Clone() ?? new GameSettings(),
        GameOverrides = settings.GameOverrides.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase)
    };
}

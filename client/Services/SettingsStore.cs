namespace RobloxAltClient.Services;

public sealed class SettingsStore
{
    private readonly RobloxAccountManager.Core.Data.SettingsStore _inner;

    public SettingsStore(string? appDataDirectory = null)
    {
        _inner = appDataDirectory is null
            ? new RobloxAccountManager.Core.Data.SettingsStore()
            : new RobloxAccountManager.Core.Data.SettingsStore(appDataDirectory);
    }

    public Task<LauncherSettings> LoadAsync() => _inner.LoadAsync();

    public Task SaveAsync(LauncherSettings settings) => _inner.SaveAsync(settings);
}

namespace RobloxAltClient.Services;

public sealed class GamePresetStore
{
    private readonly RobloxAccountManager.Core.Data.GamePresetStore _inner;

    public GamePresetStore(string? appDataDirectory = null)
    {
        _inner = appDataDirectory is null
            ? new RobloxAccountManager.Core.Data.GamePresetStore()
            : new RobloxAccountManager.Core.Data.GamePresetStore(appDataDirectory);
    }

    public Task<List<GamePreset>> LoadAsync() => _inner.LoadAsync();

    public Task SaveAsync(IEnumerable<GamePreset> presets) => _inner.SaveAsync(presets);
}

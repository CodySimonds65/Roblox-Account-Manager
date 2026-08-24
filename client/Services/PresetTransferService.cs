namespace RobloxAltClient.Services;

public static class PresetTransferService
{
    public static Task ExportAsync(string path, IEnumerable<GamePreset> presets) =>
        RobloxAccountManager.Core.Data.PresetTransferService.ExportAsync(path, presets);

    public static Task<IReadOnlyList<GamePreset>> ImportAsync(string path) =>
        RobloxAccountManager.Core.Data.PresetTransferService.ImportAsync(path);
}

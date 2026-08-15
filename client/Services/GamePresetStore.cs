using System.IO;
using System.Text.Json;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public sealed class GamePresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _presetFile;

    public GamePresetStore(string? appDataDirectory = null)
    {
        appDataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient");
        _presetFile = Path.Combine(appDataDirectory, "game-presets.json");
    }

    public async Task<List<GamePreset>> LoadAsync()
    {
        var directory = Path.GetDirectoryName(_presetFile)!;
        Directory.CreateDirectory(directory);
        return await JsonFileStore.LoadAsync(_presetFile, new List<GamePreset>(), JsonOptions);
    }

    public async Task SaveAsync(IEnumerable<GamePreset> presets)
    {
        await JsonFileStore.SaveAsync(_presetFile, presets.ToList(), JsonOptions);
    }
}

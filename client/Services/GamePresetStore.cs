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
        if (!File.Exists(_presetFile))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_presetFile);
            return await JsonSerializer.DeserializeAsync<List<GamePreset>>(stream, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<GamePreset> presets)
    {
        var directory = Path.GetDirectoryName(_presetFile)!;
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(_presetFile);
        await JsonSerializer.SerializeAsync(stream, presets, JsonOptions);
    }
}

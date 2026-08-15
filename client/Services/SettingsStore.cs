using System.IO;
using System.Text.Json;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFile;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsStore(string? appDataDirectory = null)
    {
        appDataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient");
        _settingsFile = Path.Combine(appDataDirectory, "settings.json");
    }

    public async Task<LauncherSettings> LoadAsync()
    {
        var directory = Path.GetDirectoryName(_settingsFile)!;
        Directory.CreateDirectory(directory);
        if (!File.Exists(_settingsFile))
        {
            return new LauncherSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFile);
            return await JsonSerializer.DeserializeAsync<LauncherSettings>(stream, JsonOptions) ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        await _saveLock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_settingsFile)!;
            Directory.CreateDirectory(directory);
            await using var stream = File.Create(_settingsFile);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}

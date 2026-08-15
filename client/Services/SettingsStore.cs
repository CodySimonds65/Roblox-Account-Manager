using System.IO;
using System.Text.Json;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFile;

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
        return await JsonFileStore.LoadAsync(_settingsFile, new LauncherSettings(), JsonOptions);
    }

    public async Task SaveAsync(LauncherSettings settings)
    {
        await JsonFileStore.SaveAsync(_settingsFile, settings, JsonOptions);
    }
}

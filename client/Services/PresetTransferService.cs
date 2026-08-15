using System.IO;
using System.Text.Json;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public static class PresetTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task ExportAsync(string path, IEnumerable<GamePreset> presets)
    {
        var export = presets
            .Where(preset => !preset.IsBuiltIn)
            .Select(preset => new GamePreset(preset.Name, preset.Url))
            .ToList();
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, export, JsonOptions);
    }

    public static async Task<IReadOnlyList<GamePreset>> ImportAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imported = await JsonSerializer.DeserializeAsync<List<GamePreset>>(stream, JsonOptions)
                       ?? throw new InvalidOperationException("The preset file is empty.");
        if (imported.Count > 200)
        {
            throw new InvalidOperationException("A preset file may contain at most 200 games.");
        }

        var validated = new List<GamePreset>();
        foreach (var preset in imported)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > 60 ||
                !GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out var normalizedUrl))
            {
                throw new InvalidOperationException("The preset file contains an invalid game name or Roblox URL.");
            }

            if (validated.Any(existing =>
                    string.Equals(existing.Name, preset.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            validated.Add(new GamePreset(preset.Name.Trim(), normalizedUrl));
        }

        return validated;
    }
}

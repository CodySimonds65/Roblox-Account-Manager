using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

/// <summary>
/// Updates the Roblox preferences that back the visible in-game Graphics
/// Quality and Maximum Frame Rate controls. These settings are intentionally
/// separate from temporary ClientAppSettings engine overrides.
/// </summary>
public sealed class RobloxMenuSettingsService
{
    private readonly string _settingsPath;

    public RobloxMenuSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "GlobalBasicSettings_13.xml");
    }

    public async Task<bool> ApplyAsync(
        GameSettings global,
        GameSettings? gameOverride,
        Action<string>? status = null)
    {
        var settings = GameSettings.Merge(global, gameOverride);
        if (!settings.GraphicsQuality.HasValue && !settings.FpsLimit.HasValue)
        {
            return false;
        }

        if (!RobloxClientSettingsService.TryValidateSettings(settings, out var error))
        {
            status?.Invoke($"Roblox menu settings were skipped: {error}");
            return false;
        }

        if (!File.Exists(_settingsPath))
        {
            status?.Invoke("Roblox's local preferences file was not found; launch Roblox once, then try again.");
            return false;
        }

        try
        {
            XDocument document;
            await using (var stream = new FileStream(
                             _settingsPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             4096,
                             useAsync: true))
            {
                document = await XDocument.LoadAsync(stream, LoadOptions.PreserveWhitespace, CancellationToken.None);
            }

            var properties = document
                .Descendants("Item")
                .FirstOrDefault(item => string.Equals(
                    item.Attribute("class")?.Value,
                    "UserGameSettings",
                    StringComparison.Ordinal))
                ?.Element("Properties");
            if (properties is null)
            {
                status?.Invoke("Roblox's local preferences file does not contain UserGameSettings; menu settings were skipped.");
                return false;
            }

            var changed = false;
            if (settings.GraphicsQuality is int quality)
            {
                changed |= SetNamedValue(properties, "GraphicsOptimizationMode", "1");
                changed |= SetNamedValue(properties, "SavedQualityLevel", quality.ToString(CultureInfo.InvariantCulture));
                changed |= SetNamedValue(properties, "GraphicsQualityLevel", (quality * 2 + 1).ToString(CultureInfo.InvariantCulture));
            }

            if (settings.FpsLimit is int fps)
            {
                changed |= SetNamedValue(properties, "FramerateCap", fps.ToString(CultureInfo.InvariantCulture));
            }

            if (!changed)
            {
                return false;
            }

            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var writerSettings = new XmlWriterSettings
                {
                    Async = true,
                    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    Indent = false,
                    OmitXmlDeclaration = document.Declaration is null
                };
                await using (var output = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 useAsync: true))
                using (var writer = XmlWriter.Create(output, writerSettings))
                {
                    document.Save(writer);
                }

                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            var updatedPreferences = new List<string>(2);
            if (settings.GraphicsQuality.HasValue)
            {
                updatedPreferences.Add("Graphics Quality");
            }

            if (settings.FpsLimit.HasValue)
            {
                updatedPreferences.Add("Maximum Frame Rate");
            }

            status?.Invoke($"Updated Roblox's {string.Join(" and ", updatedPreferences)} preference{(updatedPreferences.Count == 1 ? string.Empty : "s")} for the next launch.");
            return true;
        }
        catch (Exception exception)
        {
            status?.Invoke($"Could not update Roblox's menu settings; continuing with its current preferences: {exception.Message}");
            return false;
        }
    }

    private static bool SetNamedValue(XContainer properties, string name, string value)
    {
        var element = properties.Elements().FirstOrDefault(candidate =>
            string.Equals(candidate.Attribute("name")?.Value, name, StringComparison.Ordinal));
        if (element is null || string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        element.Value = value;
        return true;
    }
}

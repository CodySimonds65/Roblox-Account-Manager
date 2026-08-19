using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>
/// Applies the stable UserGameSettings values shared by Roblox macOS builds.
/// Unknown fields are reported as skipped and never overwrite the user's file.
/// </summary>
public sealed class MacRobloxSettingsAdapter : IRobloxSettingsAdapter
{
    private readonly string _settingsPath;
    private readonly string _enginePath;
    private readonly string _recoveryPath;

    public MacRobloxSettingsAdapter(string? settingsPath = null, string? enginePath = null)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Roblox");
        _settingsPath = settingsPath ?? Path.Combine(root, "GlobalBasicSettings_13.xml");
        _enginePath = enginePath ?? Path.Combine(root, "ClientSettings", "ClientAppSettings.json");
        _recoveryPath = _settingsPath + ".roblox-account-manager-recovery.json";
    }

    public IReadOnlyList<RobloxSettingCapability> Capabilities =>
    [
        new("graphics-quality", CapabilityStatus.Supported, "Applies Roblox UserGameSettings when the local XML file exposes the field."),
        new("fps", CapabilityStatus.Supported, "Applies the Roblox FramerateCap preference."),
        new("master-volume", CapabilityStatus.Supported, "Applies the Roblox MasterVolume preference."),
        new("engine-flags", CapabilityStatus.Supported, "Applies scalar engine flags when ClientAppSettings.json exists."),
        new("msaa-texture-scaling", CapabilityStatus.RequiresPermission, "Depends on the current Roblox macOS build exposing compatible engine flags.", "field-not-available")
    ];

    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_recoveryPath)) return;
        PathSafety.RejectSymlinkComponents(_recoveryPath);
        PathSafety.RejectSymlink(_recoveryPath);
        var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(
            await File.ReadAllBytesAsync(_recoveryPath, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("The Roblox settings recovery record is invalid.");
        if (snapshot.SettingsBytes is null)
        {
            if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
        }
        else
        {
            await WriteAtomicAsync(_settingsPath, snapshot.SettingsBytes, cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.EngineBytes is null)
        {
            if (File.Exists(_enginePath)) File.Delete(_enginePath);
        }
        else
        {
            await WriteAtomicAsync(_enginePath, snapshot.EngineBytes, cancellationToken).ConfigureAwait(false);
        }

        File.Delete(_recoveryPath);
    }

    public async ValueTask<RobloxSettingsApplyResult> ApplyAsync(GameSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        if (!GameSettings.TryValidate(settings, out var validation))
            return new(false, [], ["all"], "invalid-settings:" + validation);

        var applied = new List<string>();
        var skipped = new List<string>();
        if (File.Exists(_settingsPath)) PathSafety.RejectSymlink(_settingsPath);
        if (File.Exists(_enginePath)) PathSafety.RejectSymlink(_enginePath);
        byte[]? originalSettings = null;
        byte[]? pendingSettings = null;
        byte[]? originalEngine = null;
        byte[]? pendingEngine = null;
        if (File.Exists(_settingsPath))
        {
            originalSettings = await File.ReadAllBytesAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
            try
            {
                var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(originalSettings), LoadOptions.PreserveWhitespace);
                var properties = document.Descendants("Item")
                    .FirstOrDefault(x => string.Equals(x.Attribute("class")?.Value, "UserGameSettings", StringComparison.Ordinal))?
                    .Element("Properties");
                if (properties is null)
                {
                    if (settings.GraphicsQuality is not null) skipped.Add("graphics-quality");
                    if (settings.FpsLimit is not null) skipped.Add("fps");
                    if (settings.MasterVolumeLevel is not null) skipped.Add("master-volume");
                }
                else
                {
                    ApplyXml(properties, "GraphicsQuality", settings.GraphicsQuality?.ToString(CultureInfo.InvariantCulture), "graphics-quality", applied, skipped);
                    ApplyXml(properties, "FramerateCap", settings.FpsLimit?.ToString(CultureInfo.InvariantCulture), "fps", applied, skipped);
                    ApplyXml(properties, "MasterVolume", settings.MasterVolumeLevel is int volume ? (volume / 10f).ToString("0.#########", CultureInfo.InvariantCulture) : null, "master-volume", applied, skipped);
                    if (applied.Any(capability => capability is "graphics-quality" or "fps" or "master-volume"))
                        pendingSettings = System.Text.Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                skipped.Add("user-game-settings");
            }
        }
        else
        {
            if (settings.GraphicsQuality is not null || settings.FpsLimit is not null || settings.MasterVolumeLevel is not null)
                skipped.Add("user-game-settings");
        }

        var flags = BuildFlags(settings);
        if (flags.Count > 0 && File.Exists(_enginePath))
        {
            try
            {
                originalEngine = await File.ReadAllBytesAsync(_enginePath, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_enginePath, cancellationToken).ConfigureAwait(false));
                var values = document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in flags) values[pair.Key] = pair.Value;
                pendingEngine = JsonSerializer.SerializeToUtf8Bytes(values, new JsonSerializerOptions { WriteIndented = true });
                applied.Add("engine-flags");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                skipped.Add("engine-flags");
            }
        }
        else if (flags.Count > 0) skipped.Add("engine-flags");

        // Do not commit only the fields that happen to be supported. The caller needs an
        // all-or-nothing result so a failed scoped-settings application cannot launch Roblox
        // with a partially changed configuration.
        if (skipped.Count > 0)
            return new(false, [], skipped, "some-settings-skipped");

        if (pendingSettings is not null || pendingEngine is not null)
        {
            try
            {
                await WriteAtomicAsync(
                    _recoveryPath,
                    JsonSerializer.SerializeToUtf8Bytes(new RecoverySnapshot(originalSettings, originalEngine)),
                    cancellationToken).ConfigureAwait(false);
                if (pendingSettings is not null)
                    await WriteAtomicAsync(_settingsPath, pendingSettings, cancellationToken).ConfigureAwait(false);
                if (pendingEngine is not null)
                    await WriteAtomicAsync(_enginePath, pendingEngine, cancellationToken).ConfigureAwait(false);
                File.Delete(_recoveryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(false, applied, skipped, "settings-transaction-failed");
            }
        }

        return new(skipped.Count == 0, applied, skipped, skipped.Count == 0 ? null : "some-settings-skipped");
    }

    private static void ApplyXml(XContainer properties, string name, string? value, string capability, ICollection<string> applied, ICollection<string> skipped)
    {
        if (value is null) return;
        var element = properties.Elements().FirstOrDefault(x => string.Equals(x.Attribute("name")?.Value, name, StringComparison.Ordinal));
        if (element is null) skipped.Add(capability);
        else { element.Value = value; applied.Add(capability); }
    }

    private static Dictionary<string, JsonElement> BuildFlags(GameSettings settings)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (GameSettings.TryParseAdvancedFlags(settings.AdvancedFlagsJson, out var custom, out _))
            foreach (var pair in custom) result[pair.Key] = pair.Value;
        if (settings.MsaaSamples is int msaa) result["FIntDebugForceMSAASamples"] = JsonDocument.Parse(msaa.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
        if (settings.PreserveRenderingQuality is bool preserve) result["DFFlagDisableDPIScale"] = JsonDocument.Parse((!preserve).ToString()).RootElement.Clone();
        if (settings.TextureQuality is int texture)
        {
            result["DFFlagTextureQualityOverrideEnabled"] = JsonDocument.Parse("true").RootElement.Clone();
            result["DFIntTextureQualityOverride"] = JsonDocument.Parse(texture.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
        }
        return result;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record RecoverySnapshot(byte[]? SettingsBytes, byte[]? EngineBytes);
}

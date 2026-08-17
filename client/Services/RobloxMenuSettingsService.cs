using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

/// <summary>
/// Applies Roblox's visible UserGameSettings preferences. Global settings are
/// persisted, while game/profile values use a transaction for one launch.
/// </summary>
public sealed class RobloxMenuSettingsService
{
    private static readonly JsonSerializerOptions RecoveryJsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly string _recoveryPath;

    public RobloxMenuSettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "GlobalBasicSettings_13.xml");
        _recoveryPath = _settingsPath + ".roblox-alt-menu-recovery.json";
    }

    public async Task<bool> ApplyAsync(GameSettings settings, Action<string>? status = null)
    {
        if (!await RecoverPendingAsync(status))
        {
            return false;
        }

        return await ApplyDocumentAsync(settings, status, captureOriginal: false) is not null;
    }

    public Task<bool> ApplyAsync(
        GameSettings global,
        GameSettings? gameOverride,
        Action<string>? status = null)
    {
        if (!GameSettings.TryResolve(global, gameOverride, null, out var resolved, out var resolutionError))
        {
            status?.Invoke($"Roblox menu settings were skipped: {resolutionError}");
            return Task.FromResult(false);
        }

        return ApplyAsync(resolved, status);
    }

    public async Task<RobloxMenuSettingsTransaction> ApplyForLaunchAsync(
        GameSettings settings,
        Action<string>? status = null)
    {
        if (!await RecoverPendingAsync(status))
        {
            return RobloxMenuSettingsTransaction.NoOp();
        }

        var result = await ApplyDocumentAsync(settings, status, captureOriginal: true);
        return result?.Transaction ?? RobloxMenuSettingsTransaction.NoOp();
    }

    public async Task<bool> RecoverPendingAsync(Action<string>? status = null)
    {
        if (!File.Exists(_recoveryPath))
        {
            return true;
        }

        try
        {
            MenuOverlayRecoveryState? state;
            await using (var stream = File.OpenRead(_recoveryPath))
            {
                state = await JsonSerializer.DeserializeAsync<MenuOverlayRecoveryState>(stream, RecoveryJsonOptions);
            }

            if (state is null || string.IsNullOrWhiteSpace(state.OriginalContentBase64))
            {
                throw new InvalidDataException("The menu-settings recovery record was empty.");
            }

            var complete = await RestoreStateAsync(state, status);
            if (!complete)
            {
                status?.Invoke("A Roblox menu-settings recovery record was retained because one or more values could not be restored.");
                return false;
            }

            DeleteRecoveryState();
            status?.Invoke("Recovered a pending Roblox menu-settings overlay.");
            return true;
        }
        catch (Exception exception)
        {
            status?.Invoke($"Could not recover a pending Roblox menu-settings overlay: {exception.Message}");
            return false;
        }
    }

    public bool TryReadMasterVolumeLevel(out int level)
    {
        level = 0;
        if (!File.Exists(_settingsPath))
        {
            return false;
        }

        try
        {
            var document = XDocument.Load(_settingsPath, LoadOptions.PreserveWhitespace);
            var value = FindProperties(document)?
                .Elements()
                .FirstOrDefault(element => string.Equals(
                    element.Attribute("name")?.Value,
                    "MasterVolume",
                    StringComparison.Ordinal))?.Value;

            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume) ||
                float.IsNaN(volume) || float.IsInfinity(volume))
            {
                return false;
            }

            level = Math.Clamp((int)Math.Round(Math.Clamp(volume, 0f, 1f) * 10f, MidpointRounding.AwayFromZero), 0, 10);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<ApplyResult?> ApplyDocumentAsync(
        GameSettings settings,
        Action<string>? status,
        bool captureOriginal)
    {
        if (!settings.GraphicsQuality.HasValue &&
            !settings.FpsLimit.HasValue &&
            !settings.MasterVolumeLevel.HasValue)
        {
            return null;
        }

        if (!RobloxClientSettingsService.TryValidateSettings(settings, out var error))
        {
            status?.Invoke($"Roblox menu settings were skipped: {error}");
            return null;
        }

        if (!File.Exists(_settingsPath))
        {
            status?.Invoke("Roblox's local preferences file was not found; launch Roblox once, then try again.");
            return null;
        }

        try
        {
            var originalBytes = await File.ReadAllBytesAsync(_settingsPath);
            using var input = new MemoryStream(originalBytes, writable: false);
            var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
            var properties = FindProperties(document);
            if (properties is null)
            {
                status?.Invoke("Roblox's local preferences file does not contain UserGameSettings; menu settings were skipped.");
                return null;
            }

            var changes = new Dictionary<string, MenuValueChange>(StringComparer.Ordinal);
            var missingFields = new HashSet<string>(StringComparer.Ordinal);
            var updatedPreferences = ApplyValues(properties, settings, changes, missingFields);
            if (missingFields.Count > 0)
            {
                status?.Invoke($"Roblox's local preferences file does not contain {string.Join(", ", missingFields)}; those settings were skipped.");
            }

            if (updatedPreferences.Count == 0)
            {
                return null;
            }

            MenuOverlayRecoveryState? recoveryState = null;
            if (captureOriginal)
            {
                recoveryState = new MenuOverlayRecoveryState
                {
                    OriginalContentBase64 = Convert.ToBase64String(originalBytes),
                    OriginalValues = changes.ToDictionary(pair => pair.Key, pair => pair.Value.OriginalValue, StringComparer.Ordinal),
                    AppliedValues = changes.ToDictionary(pair => pair.Key, pair => pair.Value.AppliedValue, StringComparer.Ordinal)
                };
                await WriteRecoveryStateAsync(recoveryState);
            }

            try
            {
                await WriteDocumentAtomicallyAsync(document);
            }
            catch
            {
                if (captureOriginal)
                {
                    DeleteRecoveryState();
                }

                throw;
            }

            status?.Invoke($"Updated Roblox's {string.Join(" and ", updatedPreferences)} preference{(updatedPreferences.Count == 1 ? string.Empty : "s")} for the next launch.");

            return captureOriginal
                ? new ApplyResult(new RobloxMenuSettingsTransaction(this, recoveryState!, status))
                : new ApplyResult(null);
        }
        catch (Exception exception)
        {
            status?.Invoke($"Could not update Roblox's menu settings; continuing with its current preferences: {exception.Message}");
            return null;
        }
    }

    private static List<string> ApplyValues(
        XContainer properties,
        GameSettings settings,
        IDictionary<string, MenuValueChange> changes,
        ISet<string> missingFields)
    {
        var updatedPreferences = new List<string>(3);
        if (settings.GraphicsQuality is int quality)
        {
            var changed = false;
            changed |= SetNamedValue(properties, "GraphicsOptimizationMode", "1", changes, missingFields);
            changed |= SetNamedValue(properties, "SavedQualityLevel", quality.ToString(CultureInfo.InvariantCulture), changes, missingFields);
            changed |= SetNamedValue(properties, "GraphicsQualityLevel", (quality * 2 + 1).ToString(CultureInfo.InvariantCulture), changes, missingFields);
            if (changed)
            {
                updatedPreferences.Add("Graphics Quality");
            }
        }

        if (settings.FpsLimit is int fps &&
            SetNamedValue(properties, "FramerateCap", fps.ToString(CultureInfo.InvariantCulture), changes, missingFields))
        {
            updatedPreferences.Add("Maximum Frame Rate");
        }

        if (settings.MasterVolumeLevel is int volume &&
            SetNamedValue(properties, "MasterVolume", (volume / 10f).ToString("0.#########", CultureInfo.InvariantCulture), changes, missingFields))
        {
            updatedPreferences.Add("Master Volume");
        }

        return updatedPreferences;
    }

    private async Task<bool> RestoreStateAsync(MenuOverlayRecoveryState state, Action<string>? status)
    {
        var originalBytes = Convert.FromBase64String(state.OriginalContentBase64);
        if (!File.Exists(_settingsPath))
        {
            await WriteBytesAtomicallyAsync(originalBytes);
            return true;
        }

        var currentBytes = await File.ReadAllBytesAsync(_settingsPath);
        try
        {
            using var input = new MemoryStream(currentBytes, writable: false);
            var document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
            var properties = FindProperties(document);
            if (properties is null)
            {
                await WriteBytesAtomicallyAsync(originalBytes);
                return true;
            }

            var changed = false;
            var complete = true;
            foreach (var pair in state.AppliedValues)
            {
                var element = FindNamedValue(properties, pair.Key);
                if (element is null)
                {
                    status?.Invoke($"Could not restore Roblox setting '{pair.Key}' because its XML element is missing.");
                    continue;
                }

                if (string.Equals(element.Value, pair.Value, StringComparison.Ordinal))
                {
                    if (state.OriginalValues.TryGetValue(pair.Key, out var originalValue))
                    {
                        element.Value = originalValue;
                        changed = true;
                    }
                }
                else if (!state.OriginalValues.TryGetValue(pair.Key, out var originalValue) ||
                         !string.Equals(element.Value, originalValue, StringComparison.Ordinal))
                {
                    status?.Invoke($"Preserved Roblox setting '{pair.Key}' because it changed while the launch overlay was active.");
                }
            }

            if (changed)
            {
                await WriteDocumentAtomicallyAsync(document);
            }

            return complete;
        }
        catch (XmlException)
        {
            status?.Invoke("Roblox's menu-settings XML became unreadable; restoring the original snapshot.");
            await WriteBytesAtomicallyAsync(originalBytes);
            return true;
        }
    }

    private async Task WriteRecoveryStateAsync(MenuOverlayRecoveryState state)
    {
        var temporaryPath = _recoveryPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, RecoveryJsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _recoveryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteRecoveryState()
    {
        if (File.Exists(_recoveryPath))
        {
            File.Delete(_recoveryPath);
        }
    }

    private async Task WriteDocumentAtomicallyAsync(XDocument document)
    {
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
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
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
    }

    private async Task WriteBytesAtomicallyAsync(byte[] bytes)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static XElement? FindNamedValue(XContainer properties, string name) =>
        properties.Elements().FirstOrDefault(candidate =>
            string.Equals(candidate.Attribute("name")?.Value, name, StringComparison.Ordinal));

    private static XElement? FindProperties(XDocument document) =>
        document
            .Descendants("Item")
            .FirstOrDefault(item => string.Equals(
                item.Attribute("class")?.Value,
                "UserGameSettings",
                StringComparison.Ordinal))
            ?.Element("Properties");

    private static bool SetNamedValue(
        XContainer properties,
        string name,
        string value,
        IDictionary<string, MenuValueChange> changes,
        ISet<string> missingFields)
    {
        var element = FindNamedValue(properties, name);
        if (element is null)
        {
            missingFields.Add(name);
            return false;
        }

        if (string.Equals(element.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        changes[name] = new MenuValueChange(element.Value, value);
        element.Value = value;
        return true;
    }

    private sealed record MenuValueChange(string OriginalValue, string AppliedValue);

    internal sealed class MenuOverlayRecoveryState
    {
        public string OriginalContentBase64 { get; set; } = string.Empty;
        public Dictionary<string, string> OriginalValues { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AppliedValues { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record ApplyResult(RobloxMenuSettingsTransaction? Transaction);

    public sealed class RobloxMenuSettingsTransaction : IAsyncDisposable
    {
        private readonly RobloxMenuSettingsService? _owner;
        private readonly MenuOverlayRecoveryState? _state;
        private readonly Action<string>? _status;
        private int _disposed;

        internal RobloxMenuSettingsTransaction(
            RobloxMenuSettingsService owner,
            MenuOverlayRecoveryState state,
            Action<string>? status)
        {
            _owner = owner;
            _state = state;
            _status = status;
        }

        private RobloxMenuSettingsTransaction()
        {
        }

        public bool IsActive => _owner is not null && _state is not null && Volatile.Read(ref _disposed) == 0;

        public static RobloxMenuSettingsTransaction NoOp() => new();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _owner is null || _state is null)
            {
                return;
            }

            try
            {
                if (await _owner.RestoreStateAsync(_state, _status))
                {
                    _owner.DeleteRecoveryState();
                }
                else
                {
                    _status?.Invoke("The Roblox menu-settings recovery record was retained for the next startup.");
                }
            }
            catch (Exception exception)
            {
                _status?.Invoke($"Could not restore Roblox's menu settings after the launch overlay: {exception.Message}");
            }
        }
    }
}

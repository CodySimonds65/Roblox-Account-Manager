using System.IO;
using System.Text.Json;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

/// <summary>
/// Owns the small, transactional ClientAppSettings.json override used for a
/// single Roblox launch. The original file is restored as soon as the new
/// process is observed, so later accounts do not inherit another game's flags.
/// </summary>
public sealed class RobloxClientSettingsService
{
    private const string MsaaFlag = "FIntDebugForceMSAASamples";
    private const string PreserveQualityFlag = "DFFlagDisableDPIScale";
    private const string TextureEnabledFlag = "DFFlagTextureQualityOverrideEnabled";
    private const string TextureQualityFlag = "DFIntTextureQualityOverride";
    private const string FpsFlag = "DFIntTaskSchedulerTargetFps";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _recoveryPath;
    private readonly string _robloxLogDirectory;

    public RobloxClientSettingsService(string? appDataDirectory = null, string? robloxLogDirectory = null)
    {
        appDataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient");
        Directory.CreateDirectory(appDataDirectory);
        _recoveryPath = Path.Combine(appDataDirectory, "roblox-client-settings-recovery.json");
        _robloxLogDirectory = robloxLogDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "logs");
    }

    public async Task RecoverPendingAsync(Action<string>? warning = null)
    {
        if (!File.Exists(_recoveryPath))
        {
            return;
        }

        try
        {
            RecoveryState? state;
            await using (var recoveryStream = File.OpenRead(_recoveryPath))
            {
                state = await JsonSerializer.DeserializeAsync<RecoveryState>(recoveryStream, JsonOptions);
            }

            var files = state?.GetFiles().ToArray() ?? [];
            if (files.Length == 0)
            {
                throw new InvalidDataException("The recovery record was empty.");
            }

            await RestoreFilesAsync(files);
            File.Delete(_recoveryPath);
            warning?.Invoke("Restored Roblox ClientAppSettings.json after an interrupted launch.");
        }
        catch (Exception exception)
        {
            warning?.Invoke($"Could not restore a previous Roblox engine-settings file: {exception.Message}");
        }
    }

    public async Task<RobloxSettingsTransaction> ApplyAsync(
        GameSettings global,
        GameSettings? gameOverride,
        string launcherPreference,
        Action<string>? warning = null)
    {
        if (!GameSettings.TryResolve(global, gameOverride, null, out var resolved, out var resolutionError))
        {
            warning?.Invoke($"Engine settings were skipped: {resolutionError}");
            return RobloxSettingsTransaction.NoOp();
        }

        return await ApplyAsync(resolved, launcherPreference, warning);
    }

    public async Task<RobloxSettingsTransaction> ApplyAsync(
        GameSettings settings,
        string launcherPreference,
        Action<string>? warning = null)
    {
        try
        {
            if (!settings.HasOverrides)
            {
                return RobloxSettingsTransaction.NoOp();
            }

            if (!TryValidateSettings(settings, out var settingsError))
            {
                warning?.Invoke($"Engine settings were skipped: {settingsError}");
                return RobloxSettingsTransaction.NoOp();
            }

            // Graphics quality and FPS are native Roblox menu preferences. Do
            // not resolve or touch ClientAppSettings for native-only changes.
            if (BuildFlags(settings).Count == 0)
            {
                return RobloxSettingsTransaction.NoOp();
            }

            var usesBloxstrap = RobloxLauncherService.UsesBloxstrap(launcherPreference);
            var path = ResolveClientSettingsPath(launcherPreference);
            if (path is null)
            {
                warning?.Invoke("Roblox was not found, so engine settings could not be prepared.");
                return RobloxSettingsTransaction.NoOp();
            }

            var additionalRestorePaths = new List<string>();
            if (usesBloxstrap)
            {
                var deployedExecutable = RobloxLauncherService.FindBloxstrapRoblox();
                var deployedDirectory = deployedExecutable is null ? null : Path.GetDirectoryName(deployedExecutable);
                if (!string.IsNullOrWhiteSpace(deployedDirectory))
                {
                    additionalRestorePaths.Add(Path.Combine(
                        deployedDirectory,
                        "ClientSettings",
                        "ClientAppSettings.json"));
                }
            }

            return await ApplyToPathsAsync(path, settings, additionalRestorePaths, warning);
        }
        catch (Exception exception)
        {
            warning?.Invoke($"Could not update Roblox engine-settings file; continuing without overrides: {exception.Message}");
            return RobloxSettingsTransaction.NoOp();
        }
    }

    internal static string? ResolveClientSettingsPath(string launcherPreference)
    {
        if (RobloxLauncherService.UsesBloxstrap(launcherPreference))
        {
            return RobloxLauncherService.GetBloxstrapClientSettingsPath();
        }

        var executable = RobloxLauncherService.FindStandardRoblox();
        var directory = executable is null ? null : Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return Path.Combine(directory, "ClientSettings", "ClientAppSettings.json");
    }

    public async Task WaitForClientSettingsLoadAsync(
        DateTime launchStartedUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? status = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(_robloxLogDirectory))
            {
                foreach (var logPath in Directory.EnumerateFiles(_robloxLogDirectory, "*_Player_*_last.log")
                             .Where(candidate => File.GetCreationTimeUtc(candidate) >= launchStartedUtc.AddSeconds(-2))
                             .OrderByDescending(File.GetCreationTimeUtc))
                {
                    var contents = await TryReadSharedTextAsync(logPath, cancellationToken);

                    if (contents?.Contains("LoadClientSettingsFromLocal", StringComparison.Ordinal) != true)
                    {
                        continue;
                    }

                    // Roblox writes whitelist decisions immediately after the
                    // load marker. Give that line time to flush before reporting.
                    await Task.Delay(150, cancellationToken);
                    contents = await TryReadSharedTextAsync(logPath, cancellationToken) ?? contents;
                    var deniedFlags = contents
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Where(line => line.Contains("Denied local configuration for:", StringComparison.Ordinal))
                        .Select(line => line[(line.IndexOf("Denied local configuration for:", StringComparison.Ordinal) +
                                              "Denied local configuration for:".Length)..].Trim())
                        .Where(flag => flag.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    status?.Invoke("Roblox loaded the prepared engine settings.");
                    foreach (var deniedFlag in deniedFlags)
                    {
                        status?.Invoke($"Roblox rejected {DescribeFlag(deniedFlag)} because that flag is not currently whitelisted.");
                    }

                    return;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        status?.Invoke("Could not confirm that Roblox read the engine settings before the safety timeout; settings may be ignored.");
    }

    public async Task<RobloxSettingsTransaction> ApplyToPathAsync(
        string path,
        GameSettings settings,
        Action<string>? warning = null,
        IReadOnlyCollection<string>? additionalRestorePaths = null) =>
        await ApplyToPathsAsync(path, settings, additionalRestorePaths ?? [], warning);

    private async Task<RobloxSettingsTransaction> ApplyToPathsAsync(
        string path,
        GameSettings settings,
        IReadOnlyCollection<string> additionalRestorePaths,
        Action<string>? warning)
    {
        if (!TryValidateSettings(settings, out var settingsError))
        {
            warning?.Invoke($"Engine settings were skipped: {settingsError}");
            return RobloxSettingsTransaction.NoOp();
        }

        var flags = BuildFlags(settings);
        if (flags.Count == 0)
        {
            return RobloxSettingsTransaction.NoOp();
        }

        var existed = File.Exists(path);
        string originalContent = string.Empty;
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (existed)
        {
            originalContent = await File.ReadAllTextAsync(path);
            try
            {
                using var document = JsonDocument.Parse(originalContent);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    warning?.Invoke("The existing Roblox engine-settings file is not a JSON object; overrides were skipped.");
                    return RobloxSettingsTransaction.NoOp();
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    values[property.Name] = property.Value.Clone();
                }
            }
            catch (JsonException exception)
            {
                warning?.Invoke($"The existing Roblox engine-settings file is invalid JSON; overrides were skipped: {exception.Message}");
                return RobloxSettingsTransaction.NoOp();
            }
        }

        foreach (var pair in flags)
        {
            if (pair.Value is null)
            {
                values.Remove(pair.Key);
            }
            else
            {
                values[pair.Key] = pair.Value.Value.Clone();
            }
        }

        var recoveryFiles = new List<RecoveryFileState>
        {
            new(path, existed, originalContent)
        };
        foreach (var additionalPath in additionalRestorePaths
                     .Where(candidate => !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Bloxstrap's version-folder copy is derived from the Modifications
            // source. Restore it to the source's original state rather than to a
            // potentially stale copy left by an interrupted or older launch.
            recoveryFiles.Add(new RecoveryFileState(additionalPath, existed, originalContent));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteRecoveryStateAsync(new RecoveryState { Files = recoveryFiles });
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(values, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
            return new RobloxSettingsTransaction(this, recoveryFiles, _recoveryPath, warning);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            File.Delete(_recoveryPath);
            throw;
        }
    }

    internal async Task RestoreAsync(IReadOnlyList<RecoveryFileState> files, string recoveryPath)
    {
        await RestoreFilesAsync(files);
        if (File.Exists(recoveryPath))
        {
            File.Delete(recoveryPath);
        }
    }

    public static bool TryValidateSettings(GameSettings settings, out string error)
    {
        if (settings.MsaaSamples.HasValue && settings.MsaaSamples.Value is not (0 or 2 or 4 or 8))
        {
            error = "MSAA must be Automatic, Off, 2x, 4x, or 8x.";
            return false;
        }

        if (settings.TextureQuality.HasValue && settings.TextureQuality.Value is < 0 or > 6)
        {
            error = "Texture quality must be Automatic or a level from 0 through 6.";
            return false;
        }

        if (settings.GraphicsQuality.HasValue && settings.GraphicsQuality.Value is < 1 or > 10)
        {
            error = "Graphics quality must be Automatic or a level from 1 through 10.";
            return false;
        }

        if (settings.FpsLimit.HasValue && settings.FpsLimit.Value is < 30 or > 1000)
        {
            error = "FPS must be Automatic or a whole number from 30 through 1000.";
            return false;
        }

        if (settings.MasterVolumeLevel.HasValue && settings.MasterVolumeLevel.Value is < 0 or > 10)
        {
            error = "Master volume must be Automatic or a level from 0 through 10.";
            return false;
        }

        return TryParseAdvancedFlags(settings.AdvancedFlagsJson, out _, out error);
    }

    public static bool TryParseAdvancedFlags(
        string? json,
        out Dictionary<string, JsonElement> flags,
        out string error)
    {
        flags = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Engine flags must be a JSON object.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    error = "Engine flag names cannot be empty.";
                    flags.Clear();
                    return false;
                }

                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                {
                    error = $"Engine flag '{property.Name}' must use a scalar JSON value.";
                    flags.Clear();
                    return false;
                }

                flags[property.Name] = property.Value.Clone();
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = $"Engine flags JSON is invalid: {exception.Message}";
            return false;
        }
    }

    public static string FormatAdvancedFlags(string? json)
    {
        if (!TryParseAdvancedFlags(json, out var flags, out _))
        {
            return json ?? string.Empty;
        }

        return JsonSerializer.Serialize(flags, JsonOptions);
    }

    private static Dictionary<string, JsonElement?> BuildFlags(GameSettings settings)
    {
        var flags = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);
        if (TryParseAdvancedFlags(settings.AdvancedFlagsJson, out var advanced, out _))
        {
            foreach (var pair in advanced)
            {
                flags[pair.Key] = pair.Value.ValueKind == JsonValueKind.Null ? null : pair.Value;
            }
        }

        // Curated settings are applied last, intentionally taking precedence
        // over duplicate entries in the advanced JSON editor.
        SetOptionalStringFlag(flags, MsaaFlag, settings.MsaaSamples?.ToString());
        SetOptionalBooleanFlag(flags, PreserveQualityFlag, settings.PreserveRenderingQuality);
        SetOptionalStringFlag(flags, TextureEnabledFlag, settings.TextureQuality.HasValue ? "True" : null);
        SetOptionalStringFlag(flags, TextureQualityFlag, settings.TextureQuality?.ToString());
        // The curated FPS control writes Roblox's native FramerateCap setting.
        // Remove a duplicate advanced Fast Flag so the rejected legacy flag
        // cannot silently override the supported path.
        flags.Remove(FpsFlag);
        return flags;
    }

    private static void SetOptionalStringFlag(Dictionary<string, JsonElement?> flags, string key, string? value)
    {
        if (value is null)
        {
            // Automatic means the launcher does not manage this flag. Removing a
            // duplicate advanced entry here preserves any value owned by the user
            // in the existing ClientAppSettings file.
            flags.Remove(key);
            return;
        }

        flags[key] = JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
    }

    private static void SetOptionalBooleanFlag(Dictionary<string, JsonElement?> flags, string key, bool? value)
    {
        if (!value.HasValue)
        {
            flags.Remove(key);
            return;
        }

        // False is an explicit per-game disable, so it removes the flag for
        // this launch. Null remains the global/per-game Automatic state.
        flags[key] = value.Value
            ? JsonDocument.Parse(JsonSerializer.Serialize("True")).RootElement.Clone()
            : null;
    }

    private static string DescribeFlag(string flag) => flag switch
    {
        FpsFlag => "the FPS target",
        MsaaFlag => "the MSAA override",
        PreserveQualityFlag => "the rendering-quality override",
        TextureEnabledFlag or TextureQualityFlag => "the texture-quality override",
        _ => $"engine flag '{flag}'"
    };

    private static async Task<string?> TryReadSharedTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task WriteRecoveryStateAsync(RecoveryState state)
    {
        var tempPath = $"{_recoveryPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(tempPath, _recoveryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task RestoreFileAsync(string path, bool existed, string originalContent)
    {
        if (existed)
        {
            var tempPath = $"{path}.{Guid.NewGuid():N}.restore";
            try
            {
                await File.WriteAllTextAsync(tempPath, originalContent);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task RestoreFilesAsync(IEnumerable<RecoveryFileState> files)
    {
        foreach (var file in files)
        {
            await RestoreFileAsync(file.Path, file.Existed, file.OriginalContent);
        }
    }

    internal sealed record RecoveryFileState(string Path, bool Existed, string OriginalContent);

    private sealed class RecoveryState
    {
        public List<RecoveryFileState>? Files { get; set; }

        // Legacy fields keep recovery records written by earlier test builds usable.
        public string? Path { get; set; }
        public bool Existed { get; set; }
        public string OriginalContent { get; set; } = string.Empty;

        public IEnumerable<RecoveryFileState> GetFiles()
        {
            if (Files is { Count: > 0 })
            {
                return Files;
            }

            return string.IsNullOrWhiteSpace(Path)
                ? []
                : [new RecoveryFileState(Path, Existed, OriginalContent)];
        }
    }
}

public sealed class RobloxSettingsTransaction : IAsyncDisposable
{
    private readonly RobloxClientSettingsService? _owner;
    private readonly IReadOnlyList<RobloxClientSettingsService.RecoveryFileState> _files;
    private readonly string _recoveryPath;
    private readonly Action<string>? _warning;
    private bool _restored;

    private RobloxSettingsTransaction()
    {
        _owner = null;
        _files = [];
        _recoveryPath = string.Empty;
        _warning = null;
    }

    internal RobloxSettingsTransaction(
        RobloxClientSettingsService owner,
        IReadOnlyList<RobloxClientSettingsService.RecoveryFileState> files,
        string recoveryPath,
        Action<string>? warning)
    {
        _owner = owner;
        _files = files;
        _recoveryPath = recoveryPath;
        _warning = warning;
    }

    public bool IsActive => _owner is not null;

    public static RobloxSettingsTransaction NoOp() => new();

    public async ValueTask DisposeAsync()
    {
        if (_restored || _owner is null)
        {
            return;
        }

        _restored = true;
        try
        {
            await _owner.RestoreAsync(_files, _recoveryPath);
        }
        catch (Exception exception)
        {
            _warning?.Invoke(
                $"Could not restore Roblox engine settings immediately; recovery will retry next launch: {exception.Message}");
        }
    }
}

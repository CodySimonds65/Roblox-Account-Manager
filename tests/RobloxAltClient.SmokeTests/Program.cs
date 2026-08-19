using RobloxAltClient.Models;
using RobloxAltClient.Plugins;
using RobloxAltClient.Services;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RequireInvalidData(Action action, string message)
{
    try
    {
        action();
        throw new InvalidOperationException(message);
    }
    catch (InvalidDataException)
    {
        // Expected rejection.
    }
}

static async Task<T> AwaitSignalAsync<T>(Task<T> signal, TimeSpan timeout, string failureMessage)
{
    try { return await signal.WaitAsync(timeout); }
    catch (TimeoutException) { throw new InvalidOperationException(failureMessage); }
}

static async Task WriteEnvelopeAsync(Stream stream, PluginEnvelope envelope, CancellationToken cancellationToken = default)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, PluginJson.Options);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(bytes, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<PluginEnvelope?> ReadEnvelopeAsync(Stream stream, CancellationToken cancellationToken = default)
{
    var header = new byte[4];
    var offset = 0;
    while (offset < header.Length)
    {
        var read = await stream.ReadAsync(header.AsMemory(offset), cancellationToken);
        if (read == 0) return null;
        offset += read;
    }
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > PluginProtocol.MaxMessageBytes) return null;
    var bytes = new byte[length];
    offset = 0;
    while (offset < length)
    {
        var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
        if (read == 0) return null;
        offset += read;
    }
    return JsonSerializer.Deserialize<PluginEnvelope>(bytes, PluginJson.Options);
}

static async Task<PluginEnvelope?> ReadEnvelopeUntilAsync(Stream stream, string type, TimeSpan timeout)
{
    using var timeoutSource = new CancellationTokenSource(timeout);
    while (!timeoutSource.IsCancellationRequested)
    {
        try
        {
            var envelope = await ReadEnvelopeAsync(stream, timeoutSource.Token);
            if (envelope is null) return null;
            if (envelope.Type == type) return envelope;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
    return null;
}

Require(
    GamePreset.TryNormalizeRobloxGameUrl(
        "https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn",
        out var normalizedUrl),
    "A valid Roblox game URL was rejected.");
Require(normalizedUrl.Contains("77649408247578"), "The normalized URL lost its game ID.");
Require(
    !GamePreset.TryNormalizeRobloxGameUrl("http://www.roblox.com/games/123/Test", out _),
    "An insecure Roblox URL was accepted.");
Require(
    !GamePreset.TryNormalizeRobloxGameUrl("https://example.com/games/123/Test", out _),
    "A non-Roblox URL was accepted.");
Require(
    !GamePreset.TryNormalizeRobloxGameUrl("https://www.roblox.com/home", out _),
    "A non-game Roblox URL was accepted.");
Require(
    GamePreset.TryNormalizeRobloxGameUrl(
        "https://www.roblox.com/games/123456/Test?privateServerLinkCode=secret",
        out var privateServerUrl) && privateServerUrl.Contains("privateServerLinkCode=secret"),
    "A Roblox private-server link was not preserved.");

Require(
    RobloxClientSettingsService.TryParseAdvancedFlags(
        "{\"FFlagExample\": true, \"FIntExample\": 4}", out var parsedFlags, out _)
        && parsedFlags.Count == 2,
    "Valid scalar engine flags were rejected.");
Require(
    !RobloxClientSettingsService.TryParseAdvancedFlags("{\"Nested\": {}}", out _, out _),
    "Nested engine flags were accepted.");
Require(
    !RobloxClientSettingsService.TryParseAdvancedFlags("{ invalid", out _, out _),
    "Malformed engine flags were accepted.");
Require(
    !RobloxClientSettingsService.TryParseAdvancedFlags("{\"\": true}", out _, out _),
    "An empty engine-flag name was accepted.");
Require(
    !RobloxClientSettingsService.TryValidateSettings(new GameSettings { MsaaSamples = 16 }, out _),
    "An unsupported MSAA value was accepted.");
Require(
    !RobloxClientSettingsService.TryValidateSettings(new GameSettings { TextureQuality = 7 }, out _),
    "An unsupported texture-quality value was accepted.");
Require(
    !RobloxClientSettingsService.TryValidateSettings(new GameSettings { GraphicsQuality = 11 }, out _),
    "An unsupported graphics-quality value was accepted.");
Require(
    !RobloxClientSettingsService.TryValidateSettings(new GameSettings { FpsLimit = 10 }, out _),
    "An unsafe FPS value was accepted.");
Require(
    RobloxClientSettingsService.TryValidateSettings(new GameSettings { MasterVolumeLevel = 0 }, out _),
    "A muted master volume was rejected.");
Require(
    !RobloxClientSettingsService.TryValidateSettings(new GameSettings { MasterVolumeLevel = 11 }, out _),
    "An invalid master volume level was accepted.");

var runningAccountStateRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    using var runningAccounts = new RunningAccountRegistry(runningAccountStateRoot);
    runningAccounts.Register(new AccountProfile { Id = "state-test", Label = "State test" }, Process.GetCurrentProcess());
    var statePath = Path.Combine(runningAccountStateRoot, "running-accounts.json");
    Require(File.Exists(statePath), "Running-account state was not persisted.");
    using var stateDocument = JsonDocument.Parse(File.ReadAllText(statePath));
    Require(stateDocument.RootElement.GetArrayLength() == 1, "Running-account state contained an unexpected number of records.");
    Require(stateDocument.RootElement[0].GetProperty("windowHandle").ValueKind == JsonValueKind.Number,
        "Running-account HWND persistence was not written as a numeric value.");
    var snapshotJson = JsonSerializer.Serialize(new ManagedAccountSnapshot(
        "state-test", "State test", 1, 1, (nint)42, 0, 0, 100, 100, 96, false, DateTime.UtcNow, true), PluginJson.Options);
    Require(snapshotJson.Contains("\"windowHandle\":42", StringComparison.Ordinal),
        "Managed-account HWND wire serialization was not numeric.");
    var inputResultJson = JsonSerializer.Serialize(BackgroundInputResult.Failure("test", "test", (nint)7, (nint)8), PluginJson.Options);
    Require(inputResultJson.Contains("\"foregroundBefore\":7", StringComparison.Ordinal) && inputResultJson.Contains("\"foregroundAfter\":8", StringComparison.Ordinal),
        "Background input HWND wire serialization was not numeric.");

    var sdkSnapshot = new RobloxAccountManager.PluginSdk.ManagedAccountSnapshot(
        "sdk-test", "SDK test", 2, 3, (nint)0x1234, 1, 2, 300, 200, 144, false, DateTime.UtcNow, true, (nint)0x5678);
    var sdkSnapshotJson = JsonSerializer.Serialize(sdkSnapshot, RobloxAccountManager.PluginSdk.PluginJson.Options);
    Require(sdkSnapshotJson.Contains("\"windowHandle\":4660", StringComparison.Ordinal),
        "Published SDK did not serialize HWNDs as numeric values.");
    var sdkRoundTrip = JsonSerializer.Deserialize<RobloxAccountManager.PluginSdk.ManagedAccountSnapshot>(sdkSnapshotJson,
        RobloxAccountManager.PluginSdk.PluginJson.Options);
    Require(sdkRoundTrip?.WindowHandle == (nint)0x1234 && sdkRoundTrip.RootWindowHandle == (nint)0x5678,
        "Published SDK did not round-trip numeric HWNDs.");
}
finally
{
    if (Directory.Exists(runningAccountStateRoot)) Directory.Delete(runningAccountStateRoot, recursive: true);
}
Require(!new GameSettings { AdvancedFlagsJson = "{}" }.HasOverrides,
    "An empty advanced-flags object was treated as an active override.");

var exitStateRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-exit-" + Guid.NewGuid().ToString("N"));
try
{
    using var exitRegistry = new RunningAccountRegistry(exitStateRoot);
    var exitSignals = new TaskCompletionSource<ManagedAccountSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler<ManagedAccountSnapshot> exited = (_, snapshot) => exitSignals.TrySetResult(snapshot);
    exitRegistry.AccountExited += exited;
    try
    {
        using var shortLived = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The exit-detection helper process did not start.");
        exitRegistry.Register(new AccountProfile { Id = "exit-test", Label = "Exit test" }, shortLived);
        var exitedSnapshot = await AwaitSignalAsync(exitSignals.Task, TimeSpan.FromSeconds(10),
            "The running-account registry did not report the killed client.");
        Require(exitedSnapshot.AccountId == "exit-test" && exitedSnapshot.ProcessId == shortLived.Id,
            "The registry exit report carried the wrong client.");
        Require(!exitedSnapshot.IsRunning, "The registry exit report was not a final snapshot.");
    }
    finally
    {
        exitRegistry.AccountExited -= exited;
    }

    var removeSignals = new TaskCompletionSource<ManagedAccountSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    EventHandler<ManagedAccountSnapshot> removed = (_, snapshot) => removeSignals.TrySetResult(snapshot);
    exitRegistry.AccountExited += removed;
    try
    {
        exitRegistry.Register(new AccountProfile { Id = "remove-test", Label = "Remove test" }, Process.GetCurrentProcess());
        Require(exitRegistry.Remove("remove-test"), "The registry did not remove the registered account.");
        var removedSnapshot = await AwaitSignalAsync(removeSignals.Task, TimeSpan.FromSeconds(5),
            "The registry did not report the removed account.");
        Require(removedSnapshot.AccountId == "remove-test" && removedSnapshot.Label == "Remove test" && !removedSnapshot.IsRunning,
            "The registry removal report carried the wrong snapshot.");
    }
    finally
    {
        exitRegistry.AccountExited -= removed;
    }
}
finally
{
    if (Directory.Exists(exitStateRoot)) Directory.Delete(exitStateRoot, recursive: true);
}

var terminationStateRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-terminate-" + Guid.NewGuid().ToString("N"));
try
{
    using var terminationRegistry = new RunningAccountRegistry(terminationStateRoot);
    using var managedProcess = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul")
    {
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("The termination helper process did not start.");
    terminationRegistry.Register(new AccountProfile { Id = "terminate-test", Label = "Terminate test" }, managedProcess);
    Require(await terminationRegistry.TerminateAccountAsync("terminate-test"),
        "The registry did not terminate the managed helper process.");
    Require(managedProcess.HasExited, "The managed helper process remained alive after termination.");
    Require(terminationRegistry.Snapshot().Count == 0, "The terminated account remained registered.");
}
finally
{
    if (Directory.Exists(terminationStateRoot)) Directory.Delete(terminationStateRoot, recursive: true);
}

Console.WriteLine("Managed-account termination smoke tests passed.");

var legacySettings = JsonSerializer.Deserialize<LauncherSettings>("{}");
Require(legacySettings?.GameSettings is not null && legacySettings.GameOverrides is not null,
    "Legacy launcher settings did not receive game-settings defaults.");
Require(JsonSerializer.Deserialize<AccountProfile>("{\"Label\":\"Legacy\"}")?.GameSettings is null,
    "Legacy account profiles did not default to inherited settings.");

Require(
    UpdateService.TryParseReleaseVersion("v1.2.3", out var releaseVersion) && releaseVersion == new Version(1, 2, 3),
    "A valid release version was rejected.");
Require(
    UpdateService.ParseSha256($"{new string('a', 64)}  RobloxAccountManager.exe", "RobloxAccountManager.exe") == new string('a', 64),
    "A valid release checksum was rejected.");
Require(
    UpdateService.ParseSha256($"{new string('a', 63)}  RobloxAccountManager.exe", "RobloxAccountManager.exe") is null,
    "An invalid release checksum was accepted.");

var queueItem = new LaunchQueueItem(new AccountProfile { Label = "Queue test" });
Require(queueItem.Status == "WAITING", "A new launch queue item was not waiting.");
queueItem.State = LaunchQueueState.Running;
queueItem.Detail = "Roblox started";
Require(queueItem.Status == "RUNNING" && queueItem.Detail == "Roblox started", "Launch queue status did not update.");

var diagnosticReport = CompatibilityService.CreateSafeReport(
[
    new CompatibilityCheck("Client", CompatibilityCheckState.Ready, "Version 1.2.0", "Automatic updates enabled")
]);
Require(diagnosticReport.Contains("[READY] Client: Version 1.2.0"), "The diagnostic report omitted a compatibility check.");
Require(diagnosticReport.Contains("excludes account labels", StringComparison.OrdinalIgnoreCase), "The diagnostic report omitted its privacy notice.");

var testDirectory = Path.Combine(Path.GetTempPath(), $"RobloxAltClient-SmokeTests-{Guid.NewGuid():N}");
try
{
    var store = new GamePresetStore(testDirectory);
    var expected = new List<GamePreset>
    {
        new("Test Game", "https://www.roblox.com/games/123456/Test-Game"),
        new("Private Link", "https://www.roblox.com/games/987654/Test?privateServerLinkCode=abc")
    };

    await store.SaveAsync(expected);
    var loaded = await store.LoadAsync();
    Require(loaded.SequenceEqual(expected), "Saved presets did not reload correctly.");

    var settingsStore = new SettingsStore(testDirectory);
    var expectedSettings = new LauncherSettings
    {
        LaunchDelaySeconds = 5,
        LaunchTimeoutSeconds = 60,
        PreferredLauncher = "Bloxstrap",
        LastSelectedProfileIds = ["profile-one"],
        GameSettings = new GameSettings { GraphicsQuality = 7, TextureQuality = 4, MasterVolumeLevel = 6 },
        GameOverrides = new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://www.roblox.com/games/123456/Test-Game"] = new GameSettings { FpsLimit = 240, MasterVolumeLevel = 8 }
        }
    };
    await settingsStore.SaveAsync(expectedSettings);
    var loadedSettings = await settingsStore.LoadAsync();
    Require(loadedSettings.LaunchDelaySeconds == 5, "The launch delay setting did not reload.");
    Require(loadedSettings.LaunchTimeoutSeconds == 60, "The launch timeout setting did not reload.");
    Require(loadedSettings.PreferredLauncher == "Bloxstrap", "The preferred launcher setting did not reload.");
    Require(loadedSettings.LastSelectedProfileIds.SequenceEqual(["profile-one"]), "Remembered profiles did not reload.");
    Require(loadedSettings.GameSettings.TextureQuality == 4, "Global game settings did not reload.");
    Require(loadedSettings.GameSettings.GraphicsQuality == 7, "Native graphics quality did not reload.");
    Require(loadedSettings.GameSettings.MasterVolumeLevel == 6, "Global master volume did not reload.");
    Require(loadedSettings.GameOverrides["https://www.roblox.com/games/123456/Test-Game"].FpsLimit == 240,
        "Per-game overrides did not reload.");
    Require(loadedSettings.GameOverrides["https://www.roblox.com/games/123456/Test-Game"].MasterVolumeLevel == 8,
        "Per-game master volume did not reload.");
    var serializedSettings = await File.ReadAllTextAsync(Path.Combine(testDirectory, "settings.json"));
    Require(!serializedSettings.Contains("HasOverrides", StringComparison.Ordinal),
        "A computed game-settings property leaked into the saved settings file.");

    expectedSettings.LaunchDelaySeconds = 10;
    await settingsStore.SaveAsync(expectedSettings);
    await File.WriteAllTextAsync(Path.Combine(testDirectory, "settings.json"), "{ invalid json");
    var recoveredSettings = await settingsStore.LoadAsync();
    Require(recoveredSettings.LaunchDelaySeconds == 5, "Settings did not recover from the last valid backup.");

    var globalGameSettings = new GameSettings
    {
        MsaaSamples = 4,
        PreserveRenderingQuality = true,
        FpsLimit = 120,
        MasterVolumeLevel = 2,
        AdvancedFlagsJson = "{\"FFlagGlobal\": \"True\"}"
    };
    var gameOverride = new GameSettings
    {
        FpsLimit = 240,
        MasterVolumeLevel = 8,
        AdvancedFlagsJson = "{\"FFlagGame\": \"True\"}"
    };
    var mergedGameSettings = GameSettings.Merge(globalGameSettings, gameOverride);
    Require(mergedGameSettings.FpsLimit == 240, "Per-game FPS did not override the global value.");
    Require(mergedGameSettings.MasterVolumeLevel == 8, "Per-game volume did not override the global value.");
    Require(mergedGameSettings.AdvancedFlagsJson?.Contains("FFlagGlobal") == true &&
            mergedGameSettings.AdvancedFlagsJson.Contains("FFlagGame"),
        "Global and per-game advanced flags did not merge.");
    var removedAdvancedFlag = GameSettings.Merge(
        globalGameSettings,
        new GameSettings { AdvancedFlagsJson = "{\"FFlagGlobal\": null}" });
    Require(removedAdvancedFlag.AdvancedFlagsJson?.Contains("FFlagGlobal") == false,
        "A null per-game engine flag did not remove the matching global flag.");
    var automaticOverride = new GameSettings();
    var automaticMerge = GameSettings.Merge(globalGameSettings, automaticOverride);
    Require(automaticMerge.MsaaSamples == globalGameSettings.MsaaSamples && !automaticOverride.HasOverrides,
        "Automatic per-game values did not fall back to global settings.");
    var profileOverride = new GameSettings
    {
        MasterVolumeLevel = 0,
        GraphicsQuality = 5,
        AdvancedFlagsJson = "{\"FFlagProfile\": \"True\", \"FFlagGame\": null}"
    };
    var resolvedSettings = GameSettings.Resolve(globalGameSettings, gameOverride, profileOverride);
    Require(resolvedSettings.MasterVolumeLevel == 0 && resolvedSettings.GraphicsQuality == 5,
        "Profile settings did not take priority over global and game values.");
    Require(resolvedSettings.AdvancedFlagsJson?.Contains("FFlagProfile") == true &&
            resolvedSettings.AdvancedFlagsJson.Contains("FFlagGlobal") &&
            !resolvedSettings.AdvancedFlagsJson.Contains("FFlagGame"),
        "Three-level advanced flags did not resolve correctly.");
    Require(!GameSettings.TryResolve(
                 globalGameSettings,
                 new GameSettings { AdvancedFlagsJson = "{ invalid" },
                 null,
                 out _,
                 out var invalidScopeError) &&
            invalidScopeError.Contains("Game", StringComparison.OrdinalIgnoreCase),
        "Malformed lower-level advanced flags were not rejected with their scope.");
    var allGlobalSettings = new GameSettings
    {
        MsaaSamples = 2,
        PreserveRenderingQuality = false,
        GraphicsQuality = 2,
        TextureQuality = 1,
        FpsLimit = 60,
        MasterVolumeLevel = 2
    };
    var allGameSettings = new GameSettings
    {
        MsaaSamples = 4,
        PreserveRenderingQuality = true,
        GraphicsQuality = 4,
        TextureQuality = 3,
        FpsLimit = 120,
        MasterVolumeLevel = 4
    };
    var allProfileSettings = new GameSettings
    {
        MsaaSamples = 8,
        PreserveRenderingQuality = false,
        GraphicsQuality = 8,
        TextureQuality = 6,
        FpsLimit = 240,
        MasterVolumeLevel = 8
    };
    var allResolvedSettings = GameSettings.Resolve(allGlobalSettings, allGameSettings, allProfileSettings);
    Require(allResolvedSettings.MsaaSamples == 8 &&
            allResolvedSettings.PreserveRenderingQuality == false &&
            allResolvedSettings.GraphicsQuality == 8 &&
            allResolvedSettings.TextureQuality == 6 &&
            allResolvedSettings.FpsLimit == 240 &&
            allResolvedSettings.MasterVolumeLevel == 8,
        "Profile precedence did not apply to every scalar setting.");

    var robloxMenuSettingsPath = Path.Combine(testDirectory, "Roblox", "GlobalBasicSettings_13.xml");
    Directory.CreateDirectory(Path.GetDirectoryName(robloxMenuSettingsPath)!);
    const string originalMenuSettings = """
        <?xml version="1.0" encoding="utf-8"?>
        <roblox>
          <Item class="UserGameSettings">
            <Properties>
              <int name="FramerateCap">60</int>
              <float name="MasterVolume">0.100000001</float>
              <token name="GraphicsOptimizationMode">0</token>
              <int name="GraphicsQualityLevel">1</int>
              <token name="SavedQualityLevel">1</token>
              <bool name="UnrelatedPreference">true</bool>
            </Properties>
          </Item>
        </roblox>
        """;
    await File.WriteAllTextAsync(robloxMenuSettingsPath, originalMenuSettings);
    var menuMessages = new List<string>();
    var menuSettingsService = new RobloxMenuSettingsService(robloxMenuSettingsPath);
    Require(menuSettingsService.TryReadMasterVolumeLevel(out var detectedVolume) && detectedVolume == 1,
        "The existing Roblox master volume was not read as a launcher level.");
    var menuChanged = await menuSettingsService.ApplyAsync(
        new GameSettings { GraphicsQuality = 3, FpsLimit = 120 },
        new GameSettings { GraphicsQuality = 8, FpsLimit = 240, MasterVolumeLevel = 8 },
        menuMessages.Add);
    Require(menuChanged, "Native Roblox menu settings were not updated.");
    var appliedMenuSettings = await File.ReadAllTextAsync(robloxMenuSettingsPath);
    Require(appliedMenuSettings.Contains("name=\"FramerateCap\">240<"),
        "Per-game maximum frame rate did not update Roblox's native preference.");
    Require(appliedMenuSettings.Contains("name=\"GraphicsOptimizationMode\">1<"),
        "Graphics mode was not switched to Manual.");
    Require(appliedMenuSettings.Contains("name=\"SavedQualityLevel\">8<") &&
            appliedMenuSettings.Contains("name=\"GraphicsQualityLevel\">17<"),
        "Per-game graphics quality did not update Roblox's native preferences.");
    Require(appliedMenuSettings.Contains("name=\"MasterVolume\">0.8<"),
        "Per-game master volume did not update Roblox's native preference.");
    Require(appliedMenuSettings.Contains("name=\"UnrelatedPreference\">true<"),
        "Updating Roblox menu settings removed an unrelated preference.");
    Require(menuMessages.Any(message => message.Contains("next launch", StringComparison.OrdinalIgnoreCase)),
        "Updating Roblox menu settings did not report its launch timing.");
    var menuSettingsBeforeAutomatic = await File.ReadAllTextAsync(robloxMenuSettingsPath);
    Require(!await menuSettingsService.ApplyAsync(new GameSettings(), null),
        "Automatic native settings reported a file change.");
    Require(await File.ReadAllTextAsync(robloxMenuSettingsPath) == menuSettingsBeforeAutomatic,
        "Automatic native settings rewrote Roblox's preferences file.");

    await File.WriteAllTextAsync(robloxMenuSettingsPath, originalMenuSettings);
    var overlayMessages = new List<string>();
    await using (var overlay = await menuSettingsService.ApplyForLaunchAsync(
                     new GameSettings { GraphicsQuality = 9, MasterVolumeLevel = 0 },
                     overlayMessages.Add))
    {
        var overlaySettings = await File.ReadAllTextAsync(robloxMenuSettingsPath);
        Require(overlaySettings.Contains("name=\"SavedQualityLevel\">9<") &&
                overlaySettings.Contains("name=\"MasterVolume\">0<"),
            "Launch-time menu settings overlay was not applied.");
        await File.WriteAllTextAsync(
            robloxMenuSettingsPath,
            overlaySettings.Replace("name=\"UnrelatedPreference\">true<", "name=\"UnrelatedPreference\">false<"));
    }
    var restoredOverlaySettings = await File.ReadAllTextAsync(robloxMenuSettingsPath);
    Require(restoredOverlaySettings.Contains("name=\"SavedQualityLevel\">1<") &&
            restoredOverlaySettings.Contains("name=\"MasterVolume\">0.100000001<") &&
            restoredOverlaySettings.Contains("name=\"UnrelatedPreference\">false<"),
        "Launch-time menu settings overlay did not restore the original XML values.");
    Require(!File.Exists(robloxMenuSettingsPath + ".roblox-alt-menu-recovery.json"),
        "Launch-time menu settings overlay left a recovery record after restoration.");

    await File.WriteAllTextAsync(robloxMenuSettingsPath, originalMenuSettings);
    var conflictService = new RobloxMenuSettingsService(robloxMenuSettingsPath);
    var conflictTransaction = await conflictService.ApplyForLaunchAsync(new GameSettings { MasterVolumeLevel = 7 });
    var conflictOverlaySettings = await File.ReadAllTextAsync(robloxMenuSettingsPath);
    await File.WriteAllTextAsync(
        robloxMenuSettingsPath,
        conflictOverlaySettings.Replace("name=\"MasterVolume\">0.7<", "name=\"MasterVolume\">0.4<"));
    await conflictTransaction.DisposeAsync();
    var conflictRestoredSettings = await File.ReadAllTextAsync(robloxMenuSettingsPath);
    Require(conflictRestoredSettings.Contains("name=\"MasterVolume\">0.4<") &&
            !File.Exists(robloxMenuSettingsPath + ".roblox-alt-menu-recovery.json"),
        "An external Roblox volume change was not preserved safely after overlay disposal.");

    var missingVolumePath = Path.Combine(testDirectory, "Roblox", "MissingVolume.xml");
    await File.WriteAllTextAsync(missingVolumePath, originalMenuSettings.Replace("<float name=\"MasterVolume\">0.100000001</float>", string.Empty));
    var missingVolumeMessages = new List<string>();
    var missingVolumeService = new RobloxMenuSettingsService(missingVolumePath);
    Require(!await missingVolumeService.ApplyAsync(new GameSettings { MasterVolumeLevel = 5 }, missingVolumeMessages.Add),
        "A volume-only override succeeded when Roblox's volume field was missing.");
    Require(missingVolumeMessages.Any(message => message.Contains("MasterVolume", StringComparison.Ordinal)),
        "A missing Roblox volume field did not produce a readable warning.");

    await File.WriteAllTextAsync(robloxMenuSettingsPath, originalMenuSettings);
    var menuInterruptedService = new RobloxMenuSettingsService(robloxMenuSettingsPath);
    var interruptedMenuTransaction = await menuInterruptedService.ApplyForLaunchAsync(
        new GameSettings { MasterVolumeLevel = 9 });
    Require(File.Exists(robloxMenuSettingsPath + ".roblox-alt-menu-recovery.json"),
        "Menu overlay did not create an interrupted-launch recovery record.");
    var recoveringService = new RobloxMenuSettingsService(robloxMenuSettingsPath);
    var interruptedRecoveryMessages = new List<string>();
    Require(await recoveringService.RecoverPendingAsync(interruptedRecoveryMessages.Add),
        $"Interrupted menu overlay recovery did not complete: {string.Join(" | ", interruptedRecoveryMessages)}");
    Require((await File.ReadAllTextAsync(robloxMenuSettingsPath)).Contains("name=\"MasterVolume\">0.100000001<"),
        "Interrupted menu overlay recovery did not restore the original volume.");
    await interruptedMenuTransaction.DisposeAsync();

    await File.WriteAllTextAsync(robloxMenuSettingsPath, originalMenuSettings);
    var lockedMenuService = new RobloxMenuSettingsService(robloxMenuSettingsPath);
    var lockedMenuTransaction = await lockedMenuService.ApplyForLaunchAsync(
        new GameSettings { MasterVolumeLevel = 7 });
    await using (var lockedMenuStream = new FileStream(
                     robloxMenuSettingsPath,
                     FileMode.Open,
                     FileAccess.Read,
                     FileShare.None))
    {
        await lockedMenuTransaction.DisposeAsync();
    }
    Require(File.Exists(robloxMenuSettingsPath + ".roblox-alt-menu-recovery.json"),
        "A locked menu-settings restore discarded its recovery record.");
    Require(await lockedMenuService.RecoverPendingAsync(),
        "A deferred menu-settings restore did not recover after the lock was released.");
    Require(!File.Exists(robloxMenuSettingsPath + ".roblox-alt-menu-recovery.json"),
        "A successful deferred menu-settings restore left its recovery record behind.");

    var clientSettingsPath = Path.Combine(testDirectory, "Roblox", "ClientSettings", "ClientAppSettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(clientSettingsPath)!);
    const string originalClientSettings = "{\"FFlagUserSetting\":\"Keep\",\"FIntDebugForceMSAASamples\":\"1\"}";
    await File.WriteAllTextAsync(clientSettingsPath, originalClientSettings);
    var recoveryDirectory = Path.Combine(testDirectory, "Recovery");
    var clientSettingsService = new RobloxClientSettingsService(recoveryDirectory);
    await using (var automaticTransaction = await clientSettingsService.ApplyToPathAsync(
                     clientSettingsPath,
                     new GameSettings()))
    {
        Require(await File.ReadAllTextAsync(clientSettingsPath) == originalClientSettings,
            "Automatic engine settings rewrote a user-managed ClientAppSettings file.");
    }

    Require(!File.Exists(Path.Combine(recoveryDirectory, "roblox-client-settings-recovery.json")),
        "Automatic engine settings created an unnecessary recovery transaction.");

    const string dpiOriginal = "{\"DFFlagDisableDPIScale\":\"True\",\"FFlagUserSetting\":\"Keep\"}";
    await File.WriteAllTextAsync(clientSettingsPath, dpiOriginal);
    await using (var disabledScalingTransaction = await clientSettingsService.ApplyToPathAsync(
                     clientSettingsPath,
                     new GameSettings { PreserveRenderingQuality = false }))
    {
        var applied = await File.ReadAllTextAsync(clientSettingsPath);
        Require(!applied.Contains("DFFlagDisableDPIScale"),
            "An explicit per-game scaling disable did not remove the DPI-preservation flag.");
        Require(applied.Contains("FFlagUserSetting"),
            "Disabling DPI preservation removed an unrelated user flag.");
    }

    Require(await File.ReadAllTextAsync(clientSettingsPath) == dpiOriginal,
        "The DPI-preservation disable transaction did not restore the original file.");
    await File.WriteAllTextAsync(clientSettingsPath, originalClientSettings);

    await using (var automaticPrecedenceTransaction = await clientSettingsService.ApplyToPathAsync(
                     clientSettingsPath,
                     new GameSettings
                     {
                         AdvancedFlagsJson =
                             "{\"FIntDebugForceMSAASamples\":\"8\",\"FFlagLauncherOwned\":true}"
                     }))
    {
        var applied = await File.ReadAllTextAsync(clientSettingsPath);
        Require(applied.Contains("\"FIntDebugForceMSAASamples\": \"1\""),
            "Automatic MSAA removed a user-managed flag while resolving an advanced duplicate.");
        Require(applied.Contains("\"FFlagLauncherOwned\": true"),
            "An unrelated advanced flag was not applied.");
    }

    await using (var transaction = await clientSettingsService.ApplyToPathAsync(
                     clientSettingsPath,
                     new GameSettings
                     {
                         MsaaSamples = 4,
                         TextureQuality = 3,
                         FpsLimit = 144,
                         AdvancedFlagsJson = "{\"FFlagUserSetting\":\"Override\",\"FFlagCustom\":true}"
                     }))
    {
        var applied = await File.ReadAllTextAsync(clientSettingsPath);
        Require(applied.Contains("\"FFlagUserSetting\": \"Override\""), "Advanced engine flags were not applied.");
        Require(applied.Contains("\"FIntDebugForceMSAASamples\": \"4\""), "Curated MSAA did not take precedence.");
        Require(applied.Contains("\"DFIntTextureQualityOverride\": \"3\""), "Texture quality was not applied.");
        Require(!applied.Contains("DFIntTaskSchedulerTargetFps"),
            "The curated FPS setting still emitted Roblox's rejected legacy Fast Flag.");
    }

    Require(await File.ReadAllTextAsync(clientSettingsPath) == originalClientSettings,
        "ClientAppSettings.json was not restored after the launch transaction.");
    Require(!File.Exists(Path.Combine(recoveryDirectory, "roblox-client-settings-recovery.json")),
        "The completed launch left a stale engine-settings recovery marker.");

    var deployedClientSettingsPath = Path.Combine(
        testDirectory,
        "BloxstrapVersion",
        "ClientSettings",
        "ClientAppSettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(deployedClientSettingsPath)!);
    const string deployedOriginal = "{\"FFlagDeployedOriginal\":\"True\"}";
    await File.WriteAllTextAsync(deployedClientSettingsPath, deployedOriginal);
    await using (var bloxstrapTransaction = await clientSettingsService.ApplyToPathAsync(
                     clientSettingsPath,
                     new GameSettings { MsaaSamples = 2 },
                     additionalRestorePaths: [deployedClientSettingsPath]))
    {
        await File.WriteAllTextAsync(
            deployedClientSettingsPath,
            await File.ReadAllTextAsync(clientSettingsPath));
    }

    Require(await File.ReadAllTextAsync(clientSettingsPath) == originalClientSettings,
        "The Bloxstrap source settings file was not restored.");
    Require(await File.ReadAllTextAsync(deployedClientSettingsPath) == originalClientSettings,
        "The Bloxstrap version copy was not restored to its authoritative source content.");

    var robloxLogDirectory = Path.Combine(testDirectory, "RobloxLogs");
    Directory.CreateDirectory(robloxLogDirectory);
    await File.WriteAllTextAsync(
        Path.Combine(robloxLogDirectory, "0.0.0_Test_Player_0001_last.log"),
        "[FLog::Output] LoadClientSettingsFromLocal: {}\n" +
        "[FLog::FlagFetchingStarterModule] Denied local configuration for: DFIntTaskSchedulerTargetFps\n");
    var loadMessages = new List<string>();
    await new RobloxClientSettingsService(
            Path.Combine(testDirectory, "LoadConfirmationRecovery"),
            robloxLogDirectory)
        .WaitForClientSettingsLoadAsync(
            DateTime.UtcNow.AddSeconds(-2),
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            loadMessages.Add);
    Require(loadMessages.Any(message => message.Contains("loaded the prepared", StringComparison.OrdinalIgnoreCase)),
        "The Roblox log did not confirm that prepared engine settings were loaded.");
    Require(loadMessages.Any(message => message.Contains("FPS target", StringComparison.OrdinalIgnoreCase) &&
                                        message.Contains("rejected", StringComparison.OrdinalIgnoreCase)),
        "A Roblox Fast Flag whitelist rejection was not reported.");

    var invalidPath = Path.Combine(testDirectory, "invalid", "ClientAppSettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(invalidPath)!);
    await File.WriteAllTextAsync(invalidPath, "{ invalid");
    var invalidWarning = string.Empty;
    await using (var invalidTransaction = await clientSettingsService.ApplyToPathAsync(
                     invalidPath,
                     new GameSettings { MsaaSamples = 2 },
                     message => invalidWarning = message))
    {
        Require(invalidWarning.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase),
            "Invalid ClientAppSettings.json did not produce a warning.");
    }

    var interruptedPath = Path.Combine(testDirectory, "interrupted", "ClientAppSettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(interruptedPath)!);
    const string interruptedOriginal = "{\"FFlagOriginal\":\"True\"}";
    await File.WriteAllTextAsync(interruptedPath, interruptedOriginal);
    var interruptedRecoveryDirectory = Path.Combine(testDirectory, "InterruptedRecovery");
    var interruptedService = new RobloxClientSettingsService(interruptedRecoveryDirectory);
    _ = await interruptedService.ApplyToPathAsync(interruptedPath, new GameSettings { MsaaSamples = 4 });
    Require(await File.ReadAllTextAsync(interruptedPath) != interruptedOriginal,
        "The interrupted-launch test did not apply an override.");
    await new RobloxClientSettingsService(interruptedRecoveryDirectory).RecoverPendingAsync();
    Require(await File.ReadAllTextAsync(interruptedPath) == interruptedOriginal,
        "Startup recovery did not restore an interrupted engine-settings transaction.");
    Require(!File.Exists(Path.Combine(interruptedRecoveryDirectory, "roblox-client-settings-recovery.json")),
        "Startup recovery left its marker behind.");

    var lockedPath = Path.Combine(testDirectory, "locked", "ClientAppSettings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
    const string lockedOriginal = "{\"FFlagLocked\":\"True\"}";
    await File.WriteAllTextAsync(lockedPath, lockedOriginal);
    var lockedRecoveryDirectory = Path.Combine(testDirectory, "LockedRecovery");
    var lockedService = new RobloxClientSettingsService(lockedRecoveryDirectory);
    var restoreWarning = string.Empty;
    var lockedTransaction = await lockedService.ApplyToPathAsync(
        lockedPath,
        new GameSettings { MsaaSamples = 8 },
        message => restoreWarning = message);
    await using (var lockedStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        await lockedTransaction.DisposeAsync();
    }

    var lockedRecoveryPath = Path.Combine(lockedRecoveryDirectory, "roblox-client-settings-recovery.json");
    Require(File.Exists(lockedRecoveryPath), "A failed restore discarded its recovery marker.");
    Require(restoreWarning.Contains("recovery will retry", StringComparison.OrdinalIgnoreCase),
        "A failed restore did not produce a recovery warning.");
    await lockedService.RecoverPendingAsync();
    Require(await File.ReadAllTextAsync(lockedPath) == lockedOriginal,
        "A deferred engine-settings restore did not recover after the lock was released.");
    Require(!File.Exists(lockedRecoveryPath), "A successful deferred restore left its marker behind.");

    var accountStore = new AccountStore(testDirectory);
    var expectedAccounts = new List<AccountProfile>
    {
        new() { Label = "Standard", SortOrder = 0, GameSettings = new GameSettings { MasterVolumeLevel = 0 } },
        new() { Label = "Favorite", Group = "Farm", IsFavorite = true, SortOrder = 1 }
    };
    await accountStore.SaveAsync(expectedAccounts);
    var loadedAccounts = await accountStore.LoadAsync();
    Require(loadedAccounts.Count == 2 && loadedAccounts[0].IsFavorite, "Favorite profiles were not sorted first.");
    Require(loadedAccounts[0].Group == "Farm", "Account profile metadata did not reload.");
    Require(loadedAccounts[1].GameSettings?.MasterVolumeLevel == 0, "Profile settings did not reload.");

    var transferPath = Path.Combine(testDirectory, "preset-transfer.json");
    await PresetTransferService.ExportAsync(transferPath,
    [
        new GamePreset("Built in", "https://www.roblox.com/games/111/Built-In", true),
        new GamePreset("Private game", "https://www.roblox.com/games/222/Private?privateServerLinkCode=abc")
        {
            Settings = new GameSettings { FpsLimit = 240, MasterVolumeLevel = 3 }
        }
    ]);
    var transferredPresets = await PresetTransferService.ImportAsync(transferPath);
    Require(transferredPresets.Count == 1, "Preset export included built-in games.");
    Require(transferredPresets[0].Url.Contains("privateServerLinkCode=abc"), "Preset transfer lost a private-server link.");
    Require(transferredPresets[0].Settings?.FpsLimit == 240, "Preset transfer lost per-game settings.");
    Require(transferredPresets[0].Settings?.MasterVolumeLevel == 3, "Preset transfer lost per-game volume.");

    var invalidTransferPath = Path.Combine(testDirectory, "invalid-preset-transfer.json");
    await File.WriteAllTextAsync(
        invalidTransferPath,
        "[{\"Name\":\"Invalid settings\",\"Url\":\"https://www.roblox.com/games/333/Invalid\",\"Settings\":{\"FpsLimit\":1}}]");
    try
    {
        _ = await PresetTransferService.ImportAsync(invalidTransferPath);
        throw new InvalidOperationException("Preset import accepted an invalid curated engine setting.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("invalid engine settings", StringComparison.OrdinalIgnoreCase))
    {
        // Expected: imported settings use the same validation as the UI and launch path.
    }

    var pluginManifest = PluginManifestReader.Parse("""
        {
          "schemaVersion": 1,
          "id": "io.github.codysimonds65.ram.macros",
          "name": "RAM Macros",
          "version": "1.0.0",
          "contractVersion": "1.0",
          "publisher": "CodySimonds65",
          "description": "Background-safe macro recording.",
          "capabilities": ["host.accounts.read", "host.input.background"],
          "entryPoint": "ram-macros.exe",
          "autostartDefault": false
        }
        """);
    Require(pluginManifest.Id == "io.github.codysimonds65.ram.macros", "Plugin manifest id did not parse.");
    Require(pluginManifest.EntryPoint == "ram-macros.exe", "Plugin entrypoint did not parse.");
    Require(PluginInstaller.ParseHash("abc123  plugin.zip".PadLeft(64 + 2 + 10, '0')).Length == 64,
        "A valid plugin checksum was not parsed.");

    // Self-contained plugin entrypoints are currently about 154 MiB. Keep a
    // regression check at that size so the installer cannot accidentally
    // restore the old 100 MiB per-entry limit while retaining a bounded
    // expanded package.
    var largePluginArchivePath = Path.Combine(testDirectory, "large-plugin.zip");
    using (var archive = ZipFile.Open(largePluginArchivePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("ram-macros.exe", CompressionLevel.Fastest);
        using var output = entry.Open();
        var zeroes = new byte[1024 * 1024];
        for (var index = 0; index < 154; index++)
            output.Write(zeroes, 0, zeroes.Length);
    }
    using (var archive = ZipFile.OpenRead(largePluginArchivePath))
    {
        PluginInstaller.ValidateArchiveEntries(archive, Path.Combine(testDirectory, "staging"));
    }
    var extractedPluginDirectory = Path.Combine(testDirectory, "extracted-plugin");
    var largePluginArchiveBytes = await File.ReadAllBytesAsync(largePluginArchivePath);
    PluginInstaller.ExtractSafely(largePluginArchiveBytes, extractedPluginDirectory);
    Require(new FileInfo(Path.Combine(extractedPluginDirectory, "ram-macros.exe")).Length == 154L * 1024 * 1024,
        "A valid self-contained-sized plugin entry was not extracted intact.");
    var outsideDirectory = Path.Combine(testDirectory, "outside");
    var reparseRoot = Path.Combine(testDirectory, "reparse-root");
    Directory.CreateDirectory(outsideDirectory);
    try
    {
        Directory.CreateSymbolicLink(reparseRoot, outsideDirectory);
        RequireInvalidData(
            () => PluginInstaller.ExtractSafely(largePluginArchiveBytes, reparseRoot),
            "A reparse-point staging root was accepted.");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("Reparse-point smoke test skipped: symbolic-link creation is not permitted.");
    }
    catch (IOException)
    {
        Console.WriteLine("Reparse-point smoke test skipped: symbolic-link creation is unavailable.");
    }
    Require(PluginInstaller.MaxArchiveEntryBytes >= 154L * 1024 * 1024,
        "The archive entry limit is smaller than the published self-contained plugin.");
    RequireInvalidData(
        () => PluginInstaller.ValidateArchiveMetadata(
            [("oversized.exe", PluginInstaller.MaxArchiveEntryBytes + 1, 0)],
            Path.Combine(testDirectory, "staging")),
        "An oversized plugin entry was accepted.");
    RequireInvalidData(
        () => PluginInstaller.ValidateArchiveMetadata(
            [("first.bin", PluginInstaller.MaxArchiveEntryBytes, 0),
             ("second.bin", PluginInstaller.MaxArchiveExtractedBytes - PluginInstaller.MaxArchiveEntryBytes + 1, 0)],
            Path.Combine(testDirectory, "staging")),
        "An archive over the aggregate extraction limit was accepted.");
    RequireInvalidData(
        () => PluginInstaller.ValidateArchiveMetadata(
            [("../escape.exe", 1, 0)],
            Path.Combine(testDirectory, "staging")),
        "A traversal entry was accepted.");
    RequireInvalidData(
        () => PluginInstaller.ValidateArchiveMetadata(
            [("payload:stream", 1, 0)],
            Path.Combine(testDirectory, "staging")),
        "An alternate-data-stream entry was accepted.");
    RequireInvalidData(
        () => PluginInstaller.ValidateArchiveMetadata(
            [("link.exe", 1, unchecked((int)(0xA000u << 16)))],
            Path.Combine(testDirectory, "staging")),
        "A symlink entry was accepted.");
    try
    {
        _ = PluginManifestReader.Parse("{\"schemaVersion\":1,\"id\":\"bad\",\"name\":\"x\",\"version\":\"1\",\"contractVersion\":\"1\",\"publisher\":\"x\",\"description\":\"x\",\"capabilities\":[],\"entryPoint\":\"../bad.exe\"}");
        throw new InvalidOperationException("Unsafe plugin manifest path was accepted.");
    }
    catch (InvalidDataException)
    {
        // Expected: manifest paths and ids are validated before installation.
    }

    var leaseCoordinator = new PriorityInputLeaseCoordinator();
    await using (var firstLease = await leaseCoordinator.TryAcquireAsync("account", "afk", 100, TimeSpan.FromSeconds(1), CancellationToken.None)
                 ?? throw new InvalidOperationException("The first input lease was not granted."))
    {
        using var leaseCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        Require(await leaseCoordinator.TryAcquireAsync("account", "ocr", 200, TimeSpan.FromSeconds(1), leaseCancellation.Token) is null,
            "A canceled input lease unexpectedly succeeded.");
    }
    await using var recoveredLease = await leaseCoordinator.TryAcquireAsync("account", "macros", 300, TimeSpan.FromSeconds(1), CancellationToken.None)
        ?? throw new InvalidOperationException("A canceled input lease stranded the account.");

    var confirmationPath = Path.Combine(Path.GetTempPath(), $"RobloxAccountManager-update-{Guid.NewGuid():N}.ok");
    UpdateService.ConfirmUpdatedLaunch(["--confirm-update", confirmationPath]);
    Require(File.Exists(confirmationPath), "The updater did not receive its successful-start confirmation.");
    File.Delete(confirmationPath);
}
finally
{
    if (Directory.Exists(testDirectory))
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}

Console.WriteLine("Custom game preset smoke tests passed.");

var pluginRuntimeRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-runtime-" + Guid.NewGuid().ToString("N"));
try
{
    await using var runtime = new PluginRuntime(pluginRuntimeRoot);
    var host = runtime.Host;
    var pluginId = "io.github.codysimonds65.ram.afk";
    var manifestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("loopback-manifest"))).ToLowerInvariant();
    var token = host.CreateLaunchToken(pluginId, manifestHash, [PluginCapabilities.HostAccountEvents]);
    var startTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    host.BindLaunchProcess(token, Environment.ProcessId, startTicks);

    await using var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(5000);

    await WriteEnvelopeAsync(pipe, new PluginEnvelope("plugin.hello", Guid.NewGuid().ToString("N"),
        JsonSerializer.SerializeToElement(new PluginHandshake(pluginId, token, PluginProtocol.CurrentMajor,
            PluginProtocol.CurrentMinor, manifestHash, [PluginCapabilities.HostAccountEvents],
            Environment.ProcessId, startTicks), PluginJson.Options)));
    var accepted = await ReadEnvelopeUntilAsync(pipe, "host.accept", TimeSpan.FromSeconds(5));
    Require(accepted is not null, "The plugin host did not accept the loopback handshake.");

    await WriteEnvelopeAsync(pipe, new PluginEnvelope("account.events.subscribe", "subscribe-1",
        JsonSerializer.SerializeToElement(new { }, PluginJson.Options)));
    var subscribed = await ReadEnvelopeUntilAsync(pipe, "account.events.subscribed", TimeSpan.FromSeconds(5));
    Require(subscribed is not null, "The plugin host did not acknowledge the account-event subscription.");
    Require(subscribed!.RequestId == "subscribe-1", "The subscription acknowledgment lost the request id.");

    var loopbackAccountId = "loopback-" + Guid.NewGuid().ToString("N");
    runtime.Accounts.Register(new AccountProfile { Id = loopbackAccountId, Label = "Loopback" }, Process.GetCurrentProcess());
    var updated = await ReadEnvelopeUntilAsync(pipe, "account.updated", TimeSpan.FromSeconds(5));
    var updatedSnapshot = updated!.Payload.GetProperty("account").Deserialize<ManagedAccountSnapshot>(PluginJson.Options);
    Require(updatedSnapshot?.AccountId == loopbackAccountId && updatedSnapshot.Label == "Loopback",
        "The account.updated push carried the wrong account.");

    Require(runtime.Accounts.Remove(loopbackAccountId), "The loopback account was not removed.");
    var exited = await ReadEnvelopeUntilAsync(pipe, "account.exited", TimeSpan.FromSeconds(5));
    var exitedSnapshot = exited!.Payload.GetProperty("account").Deserialize<ManagedAccountSnapshot>(PluginJson.Options);
    Require(exitedSnapshot?.AccountId == loopbackAccountId && exitedSnapshot.IsRunning == false,
        "The account.exited push did not carry the final snapshot.");
}
finally
{
    if (Directory.Exists(pluginRuntimeRoot)) Directory.Delete(pluginRuntimeRoot, recursive: true);
}

Console.WriteLine("Plugin account-event push smoke tests passed.");

var pipeDescriptor = PluginHostService.CreatePipeSecurityDescriptor();
var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
var mediumSid = new SecurityIdentifier("S-1-16-8192");
Require(pipeDescriptor.DiscretionaryAcl?.Count == 1, "The plugin pipe DACL must grant exactly one ACE.");
var pipeDaclAce = pipeDescriptor.DiscretionaryAcl![0] as CommonAce
    ?? throw new InvalidOperationException("The plugin pipe DACL ACE is not an allow ACE.");
Require(pipeDaclAce.AceQualifier == AceQualifier.AccessAllowed && pipeDaclAce.SecurityIdentifier.Equals(everyoneSid),
    "The plugin pipe DACL must allow Everyone.");
Require((pipeDaclAce.AccessMask & (uint)0xC0000000) == (uint)0xC0000000,
    "The plugin pipe DACL must grant generic read and write.");
Require(pipeDescriptor.SystemAcl?.Count == 1, "The plugin pipe SACL must carry exactly one ACE.");
var pipeLabelAce = pipeDescriptor.SystemAcl![0];
Require((byte)pipeLabelAce.AceType == 0x11, "The plugin pipe SACL must carry a mandatory label ACE.");
Require(GetMandatoryLabelSid(pipeLabelAce)?.Equals(mediumSid) == true,
    "The plugin pipe mandatory label must be medium integrity.");

var labelTestRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-label-" + Guid.NewGuid().ToString("N"));
try
{
    var labelDirectory = Path.Combine(labelTestRoot, "data");
    Directory.CreateDirectory(labelDirectory);
    var labelFile = Path.Combine(labelDirectory, ".launch-token-test");
    File.WriteAllText(labelFile, "token");
    var originalFileSd = new FileInfo(labelFile).GetAccessControl().GetSecurityDescriptorBinaryForm();
    var labeledFileSd = MediumIntegrityLabel.AddMediumIntegrityLabel(originalFileSd);
    var fileDescriptor = new RawSecurityDescriptor(labeledFileSd, 0);
    Require(GetMandatoryLabelSid(fileDescriptor.SystemAcl?[0] ?? throw new InvalidOperationException("The labeled file has no SACL."))?.Equals(mediumSid) == true,
        "The launch-token file did not receive the medium integrity label.");
    Require(fileDescriptor.DiscretionaryAcl?.Count == new RawSecurityDescriptor(originalFileSd, 0).DiscretionaryAcl?.Count,
        "Labeling must not alter the file DACL.");
    Require(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(labeledFileSd, MediumIntegrityLabel.AddMediumIntegrityLabel(labeledFileSd)),
        "Reapplying the medium integrity label duplicated the label ACE.");
    var originalDirectorySd = new DirectoryInfo(labelDirectory).GetAccessControl().GetSecurityDescriptorBinaryForm();
    var labeledDirectorySd = MediumIntegrityLabel.AddMediumIntegrityLabel(originalDirectorySd);
    var directoryDescriptor = new RawSecurityDescriptor(labeledDirectorySd, 0);
    Require(GetMandatoryLabelSid(directoryDescriptor.SystemAcl?[0] ?? throw new InvalidOperationException("The labeled directory has no SACL."))?.Equals(mediumSid) == true,
        "The plugin data directory did not receive the medium integrity label.");
    MediumIntegrityLabel.Apply(labelDirectory, isDirectory: true);
    MediumIntegrityLabel.Apply(labelFile, isDirectory: false);
}
finally
{
    if (Directory.Exists(labelTestRoot)) Directory.Delete(labelTestRoot, recursive: true);
}

Console.WriteLine("Plugin host security smoke tests passed.");

var consentRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-consent-" + Guid.NewGuid().ToString("N"));
try
{
    var consentPaths = new PluginPaths(consentRoot);
    var consentPluginId = "io.github.codysimonds65.ram.macros";
    var consentCapabilities = new[] { PluginCapabilities.HostAccountsRead, PluginCapabilities.HostInputBackground };
    var firstStore = new PluginConsentStore(consentPaths);
    firstStore.Set(consentPluginId, autostart: true, consentCapabilities);
    var reloadedStore = new PluginConsentStore(consentPaths);
    var reloaded = reloadedStore.Get(consentPluginId);
    Require(reloaded.Autostart, "Plugin autostart consent did not survive a store reload.");
    Require(reloaded.GrantedCapabilities.Count == 2 &&
        reloaded.GrantedCapabilities.Contains(PluginCapabilities.HostAccountsRead) &&
        reloaded.GrantedCapabilities.Contains(PluginCapabilities.HostInputBackground),
        "Plugin capability grants did not survive a store reload.");
}
finally
{
    if (Directory.Exists(consentRoot)) Directory.Delete(consentRoot, recursive: true);
}

Console.WriteLine("Plugin consent persistence smoke tests passed.");

var inputPostRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-inputpost-" + Guid.NewGuid().ToString("N"));
try
{
    await using var inputRuntime = new PluginRuntime(inputPostRoot);
    var inputHost = inputRuntime.Host;
    var inputPluginId = "io.github.codysimonds65.ram.macros";
    var inputManifestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("loopback-input-manifest"))).ToLowerInvariant();
    var inputToken = inputHost.CreateLaunchToken(inputPluginId, inputManifestHash, [PluginCapabilities.HostInputBackground]);
    var inputStartTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    inputHost.BindLaunchProcess(inputToken, Environment.ProcessId, inputStartTicks);

    await using var inputPipe = new NamedPipeClientStream(".", inputHost.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await inputPipe.ConnectAsync(5000);

    await WriteEnvelopeAsync(inputPipe, new PluginEnvelope("plugin.hello", Guid.NewGuid().ToString("N"),
        JsonSerializer.SerializeToElement(new PluginHandshake(inputPluginId, inputToken, PluginProtocol.CurrentMajor,
            PluginProtocol.CurrentMinor, inputManifestHash, [PluginCapabilities.HostInputBackground],
            Environment.ProcessId, inputStartTicks), PluginJson.Options)));
    var inputAccepted = await ReadEnvelopeUntilAsync(inputPipe, "host.accept", TimeSpan.FromSeconds(5));
    Require(inputAccepted is not null, "The plugin host did not accept the input-post loopback handshake.");

    var inputAccountId = "input-" + Guid.NewGuid().ToString("N");
    // The smoke-test process is a console application whose windows belong to the
    // console host, so its registered account usually carries no window handle and
    // the broker answers "unavailable" before any posting. The assertions below
    // accept that path and still verify request-id correlation and validation order.
    inputRuntime.Accounts.Register(new AccountProfile { Id = inputAccountId, Label = "Input post" }, Process.GetCurrentProcess());

    var inputPostRequestId = "input-post-" + Guid.NewGuid().ToString("N");
    await WriteEnvelopeAsync(inputPipe, new PluginEnvelope("input.post", inputPostRequestId,
        JsonSerializer.SerializeToElement(new
        {
            accountId = inputAccountId,
            events = new[]
            {
                new PluginInputEvent(Kind: PluginInputKind.KeyDown, VirtualKey: 0x41, ScanCode: 0x1E, Extended: false, Button: 0, WheelDelta: 0, NormalizedX: 0, NormalizedY: 0, OffsetMicroseconds: 0),
                new PluginInputEvent(Kind: PluginInputKind.KeyUp, VirtualKey: 0x41, ScanCode: 0x1E, Extended: false, Button: 0, WheelDelta: 0, NormalizedX: 0, NormalizedY: 0, OffsetMicroseconds: 300_000)
            }
        }, PluginJson.Options)));
    var inputPosted = await ReadEnvelopeUntilAsync(inputPipe, "input.result", TimeSpan.FromSeconds(5));
    Require(inputPosted is not null, "The plugin host did not answer the valid input.post.");
    Require(inputPosted!.RequestId == inputPostRequestId, "The input.post response lost the request id.");
    var inputResult = inputPosted.Payload.Deserialize<BackgroundInputResult>(PluginJson.Options);
    Require(inputResult is not null, "The input.post response did not carry an input.result payload.");
    var acceptedInputResult = inputResult!;
    Require(acceptedInputResult.Accepted || acceptedInputResult.Code is "unavailable" or "stale-window",
        $"The valid input.post was rejected with an unexpected code: {acceptedInputResult.Code}");
    if (acceptedInputResult.Accepted)
        Require(acceptedInputResult.PostedCount == 2, "The broker did not post both paced key events.");

    var invalidPostRequestId = "input-post-invalid-" + Guid.NewGuid().ToString("N");
    await WriteEnvelopeAsync(inputPipe, new PluginEnvelope("input.post", invalidPostRequestId,
        JsonSerializer.SerializeToElement(new
        {
            accountId = inputAccountId,
            events = new[]
            {
                new PluginInputEvent(Kind: PluginInputKind.KeyDown, VirtualKey: 0x41, ScanCode: 0x1E, Extended: false, Button: 0, WheelDelta: 0, NormalizedX: 0, NormalizedY: 0, OffsetMicroseconds: 200_000),
                new PluginInputEvent(Kind: PluginInputKind.KeyUp, VirtualKey: 0x41, ScanCode: 0x1E, Extended: false, Button: 0, WheelDelta: 0, NormalizedX: 0, NormalizedY: 0, OffsetMicroseconds: 0)
            }
        }, PluginJson.Options)));
    var invalidPosted = await ReadEnvelopeUntilAsync(inputPipe, "input.result", TimeSpan.FromSeconds(5));
    Require(invalidPosted is not null, "The plugin host did not answer the invalid input.post.");
    Require(invalidPosted!.RequestId == invalidPostRequestId, "The invalid input.post response lost the request id.");
    var invalidResult = invalidPosted.Payload.Deserialize<BackgroundInputResult>(PluginJson.Options);
    Require(invalidResult?.Code == "invalid-request",
        "The input.post with descending offsets was not rejected before posting.");

    var unknownPostRequestId = "input-post-unknown-" + Guid.NewGuid().ToString("N");
    await WriteEnvelopeAsync(inputPipe, new PluginEnvelope("input.post", unknownPostRequestId,
        JsonSerializer.SerializeToElement(new
        {
            accountId = "input-unknown-" + Guid.NewGuid().ToString("N"),
            events = new[]
            {
                new PluginInputEvent(Kind: PluginInputKind.KeyDown, VirtualKey: 0x41, ScanCode: 0x1E, Extended: false, Button: 0, WheelDelta: 0, NormalizedX: 0, NormalizedY: 0, OffsetMicroseconds: 0)
            }
        }, PluginJson.Options)));
    var unknownPosted = await ReadEnvelopeUntilAsync(inputPipe, "input.result", TimeSpan.FromSeconds(5));
    Require(unknownPosted is not null, "The plugin host did not answer the unknown-account input.post.");
    Require(unknownPosted!.RequestId == unknownPostRequestId, "The unknown-account input.post response lost the request id.");
    var unknownResult = unknownPosted.Payload.Deserialize<BackgroundInputResult>(PluginJson.Options);
    Require(unknownResult?.Code == "unknown-account",
        "The input.post for an unknown account was not rejected.");

    var subscribeRequestId = "hotkey-subscribe-" + Guid.NewGuid().ToString("N");
    await WriteEnvelopeAsync(inputPipe, new PluginEnvelope("hotkey.subscribe", subscribeRequestId,
        JsonSerializer.SerializeToElement(new { virtualKeys = new[] { 0x78, 0x77 } }, PluginJson.Options)));
    await WriteEnvelopeAsync(inputPipe, new PluginEnvelope("hotkey.subscribe", subscribeRequestId + "-invalid",
        JsonSerializer.SerializeToElement(new { virtualKeys = new[] { 0, 999 } }, PluginJson.Options)));
    var subscribeRejected = await ReadEnvelopeUntilAsync(inputPipe, "host.reject", TimeSpan.FromSeconds(5));
    Require(subscribeRejected is not null, "The plugin host did not reject an invalid hotkey subscription.");
    var rejectReason = subscribeRejected!.Payload.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
    Require(rejectReason == "invalid-request", "The invalid hotkey subscription was not rejected as invalid-request.");
    // The loopback account uses this smoke-test process as a stand-in client;
    // unregister it before PluginRuntime.DisposeAsync performs managed-client
    // shutdown so the test runner itself is never terminated.
    Require(inputRuntime.Accounts.Remove(inputAccountId), "The loopback input account was not removed.");
}
finally
{
    if (Directory.Exists(inputPostRoot)) Directory.Delete(inputPostRoot, recursive: true);
}

Console.WriteLine("Plugin input post smoke tests passed.");

Require((InputSendInjector.KeyboardFlags(keyUp: false, extended: false) & 0x000B) == 0x0008,
    "A plain key down must use scan-code injection.");
Require((InputSendInjector.KeyboardFlags(keyUp: true, extended: true) & 0x000B) == 0x000B,
    "An extended key up must set extended and key-up flags.");
Require(InputSendInjector.ButtonFlag(0, down: true) == 0x0002 && InputSendInjector.ButtonFlag(0, down: false) == 0x0004,
    "Left button flags are wrong.");
Require(InputSendInjector.ButtonFlag(1, down: true) == 0x0008 && InputSendInjector.ButtonFlag(2, down: false) == 0x0040,
    "Right/middle button flags are wrong.");
Require(InputSendInjector.ButtonFlag(4, down: true) == 0, "An unsupported button must map to no flag.");
Require(unchecked((short)InputSendInjector.WheelData(120)) == 120 && unchecked((short)InputSendInjector.WheelData(-120)) == -120,
    "Wheel delta mapping is wrong.");

Console.WriteLine("Plugin input injector mapping smoke tests passed.");

Require(PluginRuntime.SelectInputDeliveryMode(embedded: true, selectedVisible: true, hostForeground: true) ==
        PluginRuntime.InputDeliveryMode.GuardedReal,
    "A visible embedded foreground client did not select guarded real input.");
Require(PluginRuntime.SelectInputDeliveryMode(embedded: true, selectedVisible: false, hostForeground: true) ==
        PluginRuntime.InputDeliveryMode.BackgroundMessage,
    "A hidden embedded client selected system-wide input.");
Require(PluginRuntime.SelectInputDeliveryMode(embedded: true, selectedVisible: true, hostForeground: false) ==
        PluginRuntime.InputDeliveryMode.BackgroundMessage,
    "An embedded client selected system-wide input while another application was foreground.");
Require(PluginRuntime.SelectInputDeliveryMode(embedded: false, selectedVisible: false, hostForeground: false) ==
        PluginRuntime.InputDeliveryMode.BackgroundMessage,
    "A non-embedded client did not select background input.");

var routeExpected = new ManagedAccountSnapshot(
    "route-test", "Route test", 314, 159, (nint)0x1111, 0, 0, 800, 600, 96, false,
    DateTime.UtcNow, true, (nint)0x2222);
Require(PluginRuntime.MatchesInputTarget(routeExpected, routeExpected with { LastActivityUtc = DateTime.UtcNow }, (nint)0x2222),
    "A current input target was rejected.");
Require(!PluginRuntime.MatchesInputTarget(routeExpected, routeExpected with { ProcessStartTimeUtcTicks = 160 }, (nint)0x2222),
    "A reused process identity was accepted for real input.");
Require(!PluginRuntime.MatchesInputTarget(routeExpected, routeExpected with { RootWindowHandle = (nint)0x3333 }, (nint)0x2222),
    "A changed embedded HWND was accepted for real input.");
Require(!PluginRuntime.MatchesInputTarget(routeExpected, routeExpected with { IsRunning = false }, (nint)0x2222),
    "An exited input target was accepted for real input.");

Console.WriteLine("Plugin input routing smoke tests passed.");

using (var nativeHost = NativeEmbeddingTestWindow.CreateHost())
using (var firstRoot = NativeEmbeddingTestWindow.CreateRoot(-31900, -31900, 800, 600))
using (var secondRoot = NativeEmbeddingTestWindow.CreateRoot(-31800, -31800, 1024, 768))
{
    var firstOriginalStyle = firstRoot.Style;
    var firstOriginalBounds = firstRoot.Bounds;
    var secondOriginalStyle = secondRoot.Style;
    var secondOriginalBounds = secondRoot.Bounds;
    var embeddings = new ClientEmbeddingService();
    embeddings.SetHostWindow(nativeHost.Handle);

    Require(!embeddings.TryEmbed("stale-process", firstRoot.Handle, Environment.ProcessId + 1),
        "Embedding accepted a window owned by an unexpected process.");
    Require(embeddings.TryEmbed("native-first", firstRoot.Handle, Environment.ProcessId),
        "The first native test window could not be embedded.");
    Require(embeddings.TryEmbed("native-second", secondRoot.Handle, Environment.ProcessId),
        "The second native test window could not be embedded.");
    Require(firstRoot.Parent == nativeHost.Handle && secondRoot.Parent == nativeHost.Handle,
        "Embedded windows were not parented beneath the native Clients host.");
    Require(firstRoot.HasChildStyle && secondRoot.HasChildStyle,
        "Embedded windows did not receive the native child-window style.");

    embeddings.ShowOnly("native-first");
    Require(embeddings.IsVisible("native-first") && firstRoot.Visible && !secondRoot.Visible,
        "Selecting the first native client did not hide every other embedded client.");
    embeddings.ShowOnly("native-second");
    Require(embeddings.IsVisible("native-second") && secondRoot.Visible && !firstRoot.Visible,
        "Selecting the second native client did not transfer exclusive visibility.");

    Require(embeddings.TryUnembed("native-first"), "The first native test window could not be unembedded.");
    Require(firstRoot.Parent == nint.Zero && firstRoot.Style == firstOriginalStyle &&
            firstRoot.Bounds == firstOriginalBounds && firstRoot.Visible,
        "Unembedding did not restore the first window's parent, style, placement, and visibility.");

    embeddings.ReleaseHostWindow(nativeHost.Handle);
    Require(secondRoot.Parent == nint.Zero && secondRoot.Style == secondOriginalStyle &&
            secondRoot.Bounds == secondOriginalBounds && secondRoot.Visible,
        "Destroying the native host did not restore its remaining embedded window.");
    Require(embeddings.EmbeddedAccountIds().Length == 0,
        "Releasing the native host left stale embedded-account state.");
}

Console.WriteLine("Native client host lifecycle smoke tests passed.");

var autopsyRoot = Path.Combine(Path.GetTempPath(), "RobloxAltClient-autopsy-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(autopsyRoot);
    var autopsySessionStart = new DateTime(2026, 8, 19, 1, 38, 57, DateTimeKind.Utc);
    var autopsySnapshot = new ManagedAccountSnapshot(
        "autopsy-test",
        "Autopsy test",
        4242,
        autopsySessionStart.Ticks,
        nint.Zero,
        0,
        0,
        100,
        100,
        96,
        false,
        autopsySessionStart,
        true);
    var autopsyLogPath = Path.Combine(
        autopsyRoot,
        $"0.734.0.7340917_{autopsySessionStart:yyyyMMddTHHmmss}Z_Player_4367A_last.log");
    var sessionLines = new List<string>();
    for (var index = 0; index < 700; index++)
    {
        sessionLines.Add($"{autopsySessionStart.AddSeconds(index).ToUniversalTime():O} [FLog::Graphics] RenderView frame {index}");
    }
    sessionLines.Add("2026-08-19T01:39:04.305Z [FLog::UpdateController] Update check thread: updateRequired TRUE");
    sessionLines.Add("2026-08-19T01:39:04.305Z [FLog::UpdateController] Update mode is chosen as FORCE. Telemetry sent");
    sessionLines.Add("2026-08-19T01:39:04.111Z [FLog::Output] RobloxChannel has been set to zfrmparticlesaug18");
    sessionLines.Add("2026-08-19T01:39:04.704Z [FLog::Network] Sending disconnect with reason: 285");
    await File.WriteAllLinesAsync(autopsyLogPath, sessionLines);

    var autopsyLines = RobloxLogAutopsy.Autopsy(autopsySnapshot, autopsyRoot);
    Require(autopsyLines.Count > 0, "The Roblox log autopsy found no session log.");
    Require(autopsyLines.Any(line => line.Contains("updater", StringComparison.OrdinalIgnoreCase) &&
                                     line.Contains("REQUIRED", StringComparison.Ordinal)),
        "The Roblox log autopsy did not detect the force-update shutdown.");
    Require(autopsyLines.Any(line => line.Contains("zfrmparticlesaug18", StringComparison.Ordinal)),
        "The Roblox log autopsy did not report the Roblox channel.");
    Require(autopsyLines.Any(line => line.Contains("285", StringComparison.Ordinal)),
        "The Roblox log autopsy did not report the disconnect reason.");

    var unrelatedSnapshot = autopsySnapshot with { ProcessStartTimeUtcTicks = new DateTime(2026, 8, 19, 3, 0, 0, DateTimeKind.Utc).Ticks };
    var missingAutopsyLines = RobloxLogAutopsy.Autopsy(unrelatedSnapshot, autopsyRoot);
    Require(missingAutopsyLines.Count == 1 && missingAutopsyLines[0].Contains("No Roblox session log", StringComparison.Ordinal),
        "The Roblox log autopsy did not report a missing session log.");
}
finally
{
    if (Directory.Exists(autopsyRoot)) Directory.Delete(autopsyRoot, recursive: true);
}

Console.WriteLine("Roblox log autopsy smoke tests passed.");

static SecurityIdentifier? GetMandatoryLabelSid(GenericAce ace)
{
    var binary = new byte[ace.BinaryLength];
    ace.GetBinaryForm(binary, 0);
    return new SecurityIdentifier(binary, 8);
}

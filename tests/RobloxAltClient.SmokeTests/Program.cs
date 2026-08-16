using RobloxAltClient.Models;
using RobloxAltClient.Services;
using System.Text.Json;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
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
Require(!new GameSettings { AdvancedFlagsJson = "{}" }.HasOverrides,
    "An empty advanced-flags object was treated as an active override.");

var legacySettings = JsonSerializer.Deserialize<LauncherSettings>("{}");
Require(legacySettings?.GameSettings is not null && legacySettings.GameOverrides is not null,
    "Legacy launcher settings did not receive game-settings defaults.");

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
        GameSettings = new GameSettings { GraphicsQuality = 7, TextureQuality = 4 },
        GameOverrides = new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://www.roblox.com/games/123456/Test-Game"] = new GameSettings { FpsLimit = 240 }
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
    Require(loadedSettings.GameOverrides["https://www.roblox.com/games/123456/Test-Game"].FpsLimit == 240,
        "Per-game overrides did not reload.");
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
        AdvancedFlagsJson = "{\"FFlagGlobal\": \"True\"}"
    };
    var gameOverride = new GameSettings
    {
        FpsLimit = 240,
        AdvancedFlagsJson = "{\"FFlagGame\": \"True\"}"
    };
    var mergedGameSettings = GameSettings.Merge(globalGameSettings, gameOverride);
    Require(mergedGameSettings.FpsLimit == 240, "Per-game FPS did not override the global value.");
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

    var robloxMenuSettingsPath = Path.Combine(testDirectory, "Roblox", "GlobalBasicSettings_13.xml");
    Directory.CreateDirectory(Path.GetDirectoryName(robloxMenuSettingsPath)!);
    const string originalMenuSettings = """
        <?xml version="1.0" encoding="utf-8"?>
        <roblox>
          <Item class="UserGameSettings">
            <Properties>
              <int name="FramerateCap">60</int>
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
    var menuChanged = await menuSettingsService.ApplyAsync(
        new GameSettings { GraphicsQuality = 3, FpsLimit = 120 },
        new GameSettings { GraphicsQuality = 8, FpsLimit = 240 },
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
    Require(appliedMenuSettings.Contains("name=\"UnrelatedPreference\">true<"),
        "Updating Roblox menu settings removed an unrelated preference.");
    Require(menuMessages.Any(message => message.Contains("next launch", StringComparison.OrdinalIgnoreCase)),
        "Updating Roblox menu settings did not report its launch timing.");
    var menuSettingsBeforeAutomatic = await File.ReadAllTextAsync(robloxMenuSettingsPath);
    Require(!await menuSettingsService.ApplyAsync(new GameSettings(), null),
        "Automatic native settings reported a file change.");
    Require(await File.ReadAllTextAsync(robloxMenuSettingsPath) == menuSettingsBeforeAutomatic,
        "Automatic native settings rewrote Roblox's preferences file.");

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
        new() { Label = "Standard", SortOrder = 0 },
        new() { Label = "Favorite", Group = "Farm", IsFavorite = true, SortOrder = 1 }
    };
    await accountStore.SaveAsync(expectedAccounts);
    var loadedAccounts = await accountStore.LoadAsync();
    Require(loadedAccounts.Count == 2 && loadedAccounts[0].IsFavorite, "Favorite profiles were not sorted first.");
    Require(loadedAccounts[0].Group == "Farm", "Account profile metadata did not reload.");

    var transferPath = Path.Combine(testDirectory, "preset-transfer.json");
    await PresetTransferService.ExportAsync(transferPath,
    [
        new GamePreset("Built in", "https://www.roblox.com/games/111/Built-In", true),
        new GamePreset("Private game", "https://www.roblox.com/games/222/Private?privateServerLinkCode=abc")
        {
            Settings = new GameSettings { FpsLimit = 240 }
        }
    ]);
    var transferredPresets = await PresetTransferService.ImportAsync(transferPath);
    Require(transferredPresets.Count == 1, "Preset export included built-in games.");
    Require(transferredPresets[0].Url.Contains("privateServerLinkCode=abc"), "Preset transfer lost a private-server link.");
    Require(transferredPresets[0].Settings?.FpsLimit == 240, "Preset transfer lost per-game settings.");

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

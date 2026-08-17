using RobloxAltClient.Models;
using RobloxAltClient.Plugins;
using RobloxAltClient.Services;
using System.IO.Compression;
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
Require(
    RobloxClientSettingsService.TryValidateSettings(new GameSettings { MasterVolumeLevel = 0 }, out _),
    "A muted master volume was rejected.");
Require(
    !RobloxClientSettingsService.TryValidateSettings(new GameSettings { MasterVolumeLevel = 11 }, out _),
    "An invalid master volume level was accepted.");
Require(!new GameSettings { AdvancedFlagsJson = "{}" }.HasOverrides,
    "An empty advanced-flags object was treated as an active override.");

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
    // regression check so the installer cannot accidentally restore the old
    // 100 MiB per-entry limit while retaining a bounded expanded package.
    var largePluginArchivePath = Path.Combine(testDirectory, "large-plugin.zip");
    using (var archive = ZipFile.Open(largePluginArchivePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("ram-macros.exe", CompressionLevel.Fastest);
        using var output = entry.Open();
        var zeroes = new byte[1024 * 1024];
        for (var index = 0; index <= 100; index++)
            output.Write(zeroes, 0, zeroes.Length);
    }
    using (var archive = ZipFile.OpenRead(largePluginArchivePath))
    {
        PluginInstaller.ValidateArchiveEntries(archive, Path.Combine(testDirectory, "staging"));
    }
    Require(PluginInstaller.MaxArchiveEntryBytes >= 154L * 1024 * 1024,
        "The archive entry limit is smaller than the published self-contained plugin.");
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

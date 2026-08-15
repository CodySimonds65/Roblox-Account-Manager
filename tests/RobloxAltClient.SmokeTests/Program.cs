using RobloxAltClient.Models;
using RobloxAltClient.Services;

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
    UpdateService.TryParseReleaseVersion("v1.2.3", out var releaseVersion) && releaseVersion == new Version(1, 2, 3),
    "A valid release version was rejected.");
Require(
    UpdateService.ParseSha256($"{new string('a', 64)}  RobloxAltClient.exe", "RobloxAltClient.exe") == new string('a', 64),
    "A valid release checksum was rejected.");
Require(
    UpdateService.ParseSha256($"{new string('a', 63)}  RobloxAltClient.exe", "RobloxAltClient.exe") is null,
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
        LastSelectedProfileIds = ["profile-one"]
    };
    await settingsStore.SaveAsync(expectedSettings);
    var loadedSettings = await settingsStore.LoadAsync();
    Require(loadedSettings.LaunchDelaySeconds == 5, "The launch delay setting did not reload.");
    Require(loadedSettings.LaunchTimeoutSeconds == 60, "The launch timeout setting did not reload.");
    Require(loadedSettings.PreferredLauncher == "Bloxstrap", "The preferred launcher setting did not reload.");
    Require(loadedSettings.LastSelectedProfileIds.SequenceEqual(["profile-one"]), "Remembered profiles did not reload.");

    expectedSettings.LaunchDelaySeconds = 10;
    await settingsStore.SaveAsync(expectedSettings);
    await File.WriteAllTextAsync(Path.Combine(testDirectory, "settings.json"), "{ invalid json");
    var recoveredSettings = await settingsStore.LoadAsync();
    Require(recoveredSettings.LaunchDelaySeconds == 5, "Settings did not recover from the last valid backup.");

    var accountStore = new AccountStore(testDirectory);
    var expectedAccounts = new List<AccountProfile>
    {
        new() { Label = "Favorite", Group = "Farm", IsFavorite = true, SortOrder = 0 }
    };
    await accountStore.SaveAsync(expectedAccounts);
    var loadedAccounts = await accountStore.LoadAsync();
    Require(loadedAccounts.Count == 1 && loadedAccounts[0].IsFavorite, "Account profile metadata did not reload.");

    var transferPath = Path.Combine(testDirectory, "preset-transfer.json");
    await PresetTransferService.ExportAsync(transferPath,
    [
        new GamePreset("Built in", "https://www.roblox.com/games/111/Built-In", true),
        new GamePreset("Private game", "https://www.roblox.com/games/222/Private?privateServerLinkCode=abc")
    ]);
    var transferredPresets = await PresetTransferService.ImportAsync(transferPath);
    Require(transferredPresets.Count == 1, "Preset export included built-in games.");
    Require(transferredPresets[0].Url.Contains("privateServerLinkCode=abc"), "Preset transfer lost a private-server link.");

    var confirmationPath = Path.Combine(Path.GetTempPath(), $"RobloxAltClient-update-{Guid.NewGuid():N}.ok");
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

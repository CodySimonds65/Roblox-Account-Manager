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
}
finally
{
    if (Directory.Exists(testDirectory))
    {
        Directory.Delete(testDirectory, recursive: true);
    }
}

Console.WriteLine("Custom game preset smoke tests passed.");

using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAltClient.Services;
using GameSettings = RobloxAccountManager.Core.Models.GameSettings;

namespace RobloxAltClient.SmokeTests;

[TestClass]
public sealed class RobloxLaunchRoutingTests
{
    [TestMethod]
    public void MissingUriChannelUsesRobloxsRegisteredChannelButExplicitProductionWins()
    {
        Assert.AreEqual("zallocprofile32stack64mb", RobloxLauncherService.GetLaunchChannel(
            "roblox-player:1+launchmode:play", "zallocprofile32stack64mb"));
        Assert.AreEqual("", RobloxLauncherService.GetLaunchChannel(
            "roblox-player:1+launchmode:play+channel:", "zallocprofile32stack64mb"));
        Assert.AreEqual("", RobloxLauncherService.GetLaunchChannel(
            "roblox-player:1+launchmode:play+channel:LIVE", "zallocprofile32stack64mb"));
    }

    [TestMethod]
    public async Task EngineOverridesForAnotherChannelAreRestoredAndReportedBeforeLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-channel-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "production", "ClientSettings", "ClientAppSettings.json");
        var messages = new List<string>();
        try
        {
            var service = new RobloxClientSettingsService(Path.Combine(root, "manager"));
            await using var transaction = await service.ApplyToPathAsync(settingsPath,
                new GameSettings { MsaaSamples = 2 }, messages.Add);
            Assert.IsTrue(transaction.IsActive);
            await transaction.EnsureMatchesPlayerAsync(Path.Combine(root, "production", "RobloxPlayerBeta.exe"));
            Assert.IsTrue(transaction.IsActive);
            await transaction.EnsureMatchesPlayerAsync(Path.Combine(root, "test-channel", "RobloxPlayerBeta.exe"));
            Assert.IsFalse(transaction.IsActive);
            Assert.IsFalse(File.Exists(settingsPath), "The other channel's override must be restored before launching.");
            Assert.IsTrue(messages.Any(message => message.Contains("different Roblox build", StringComparison.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("roblox-player:1+launchmode:play+channel:zallocprofile32stack64mb", "zallocprofile32stack64mb")]
    [DataRow("roblox-player:1+launchmode:play", "")]
    [DataRow("roblox-player:1+channel:LIVE", "")]
    [DataRow("roblox-player:1+channel:production", "")]
    [DataRow("roblox://experiences/start?placeId=1&channel=zlazy-sm-c", "zlazy-sm-c")]
    public void ChannelIsReadWithoutChangingTheLaunchRequest(string uri, string channel)
    {
        Assert.AreEqual(channel, RobloxLauncherService.GetLaunchChannel(uri));
    }

    [TestMethod]
    [DataRow("roblox-player:1+channel:one+channel:two")]
    [DataRow("roblox-player:1+channel:..%2Fother")]
    [DataRow("roblox://experiences/start?channel=one&channel=two")]
    public void InvalidChannelsAreRejected(string uri)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => RobloxLauncherService.GetLaunchChannel(uri));
    }

    [TestMethod]
    public void InstalledVersionLookupRequiresTheExactVersionDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-build-" + Guid.NewGuid().ToString("N"));
        var player = Path.Combine(root, "version-current", "RobloxPlayerBeta.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(player)!);
        try
        {
            File.WriteAllText(player, "current");
            Assert.AreEqual(player, RobloxLauncherService.FindInstalledVersion("version-current", [root]));
            Assert.IsNull(RobloxLauncherService.FindInstalledVersion("version-other", [root]));
            Assert.ThrowsExactly<InvalidDataException>(() => RobloxLauncherService.FindInstalledVersion("version-../current", [root]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void AutoUsesNativeResolutionWhenWindowsPointsToRoblox()
    {
        Assert.IsTrue(RobloxLauncherService.ShouldUseNativePlayer("Auto", true));
        Assert.IsTrue(RobloxLauncherService.ShouldUseNativePlayer("standard", false));
        Assert.IsFalse(RobloxLauncherService.ShouldUseNativePlayer("Auto", false));
        Assert.IsFalse(RobloxLauncherService.ShouldUseNativePlayer("Bloxstrap", true));
    }

    [TestMethod]
    public async Task DifferentAccountChannelsUseTheirOwnInstalledBuilds()
    {
        var requestedChannels = new List<string>();
        Task<string> Query(string channel, CancellationToken token)
        {
            requestedChannels.Add(channel);
            return Task.FromResult(channel.Length == 0 ? "version-production" : "version-test");
        }
        string? Find(string version) => version switch
        {
            "version-production" => @"C:\Program Files\Roblox\Versions\version-production\RobloxPlayerBeta.exe",
            "version-test" => @"C:\Program Files\Roblox\Versions\version-test\RobloxPlayerBeta.exe",
            _ => null
        };
        foreach (var channel in new[] { "zallocprofile32stack64mb", "", "" })
        {
            var path = await RobloxLauncherService.ResolveNativePlayerAsync(channel, Query, Find,
                () => @"C:\AppData\Roblox\Versions\version-old\RobloxPlayerBeta.exe", () => true, null, default);
            Assert.AreEqual(Find(channel.Length == 0 ? "version-production" : "version-test"), path);
        }
        CollectionAssert.AreEqual(new[] { "zallocprofile32stack64mb", "", "" }, requestedChannels);
    }

    [TestMethod]
    public async Task MissingRequiredBuildDoesNotLaunchAnUpdaterWhileOtherClientsRun()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RobloxLauncherService.ResolveNativePlayerAsync("test", (_, _) => Task.FromResult("version-missing"),
                _ => null, () => "old-player", () => true, null, default));
    }

    [TestMethod]
    public async Task FailedVersionCheckDoesNotRiskExistingClients()
    {
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RobloxLauncherService.ResolveNativePlayerAsync("", (_, _) => throw new System.Net.Http.HttpRequestException("offline"),
                _ => null, () => "old-player", () => true, null, default));
    }

    [TestMethod]
    public async Task FirstClientCanPerformItsNormalUpdateWhenRequiredBuildIsMissing()
    {
        var messages = new List<string>();
        var path = await RobloxLauncherService.ResolveNativePlayerAsync("", (_, _) => Task.FromResult("version-missing"),
            _ => null, () => "old-player", () => false, messages.Add, default);
        Assert.AreEqual("old-player", path);
        Assert.IsTrue(messages.Any(message => message.Contains("update", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task CancellationDuringVersionLookupPreventsALateLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            RobloxLauncherService.ResolveNativePlayerAsync("", (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult("version-test");
            }, _ => "test-player", () => "old-player", () => false, null, cancellation.Token));
    }
}

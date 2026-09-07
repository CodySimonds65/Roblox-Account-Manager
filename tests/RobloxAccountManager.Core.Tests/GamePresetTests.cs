using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Core.Tests;

[TestClass]
public sealed class GamePresetTests
{
    [TestMethod]
    [DataRow("https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn", "https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn")]
    [DataRow(" https://roblox.com/games/123/Test ", "https://www.roblox.com/games/123/Test")]
    [DataRow("https://www.roblox.com/games/123456/Test?privateServerLinkCode=secret", "https://www.roblox.com/games/123456/Test?privateServerLinkCode=secret")]
    [DataRow("https://www.roblox.com/share?code=share-code&type=Server", "https://www.roblox.com/share?code=share-code&type=Server")]
    [DataRow("https://roblox.com/share?code=share-code%2Bwith%2Fsymbols&type=server&source=invite", "https://www.roblox.com/share?code=share-code%2Bwith%2Fsymbols&type=server&source=invite")]
    public void NormalizationPreservesDestinationAndPrivateServerQuery(string input, string expected)
    {
        Assert.IsTrue(GamePreset.TryNormalizeRobloxGameUrl(input, out var actual));
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("http://www.roblox.com/games/123/Test")]
    [DataRow("https://example.com/games/123/Test")]
    [DataRow("https://roblox.com.evil.example/games/123")]
    [DataRow("https://www.roblox.com/home")]
    [DataRow("https://www.roblox.com/games/not-a-number/Test")]
    [DataRow("http://www.roblox.com/share?code=secret&type=Server")]
    [DataRow("https://www.roblox.com/share?type=Server")]
    [DataRow("https://www.roblox.com/share?code=secret&type=Experience")]
    public void InvalidDestinationsAreRejectedWithoutAnOutputUrl(string input)
    {
        Assert.IsFalse(GamePreset.TryNormalizeRobloxGameUrl(input, out var actual));
        Assert.AreEqual(string.Empty, actual);
    }
}

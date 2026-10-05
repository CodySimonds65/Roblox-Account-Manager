using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.Desktop.Services;

namespace RobloxAccountManager.Desktop.Tests;

[TestClass]
public sealed class RobloxPlayControlTests
{
    [TestMethod]
    [DataRow("clicked", RobloxPlayControlStatus.Clicked)]
    [DataRow("\"not-found\"", RobloxPlayControlStatus.NotFound)]
    [DataRow("wrong-origin", RobloxPlayControlStatus.WrongOrigin)]
    [DataRow("arbitrary page text", RobloxPlayControlStatus.Unknown)]
    [DataRow(null, RobloxPlayControlStatus.Unknown)]
    public void ParsesWebViewResults(string? value, RobloxPlayControlStatus expected) =>
        Assert.AreEqual(expected, RobloxPlayControl.ParseResult(value));

    [TestMethod]
    [DataRow("\"roblox-player:1+gameinfo:script-hook-ticket\"", "roblox-player:1+gameinfo:script-hook-ticket")]
    [DataRow("roblox:1+gameinfo:ticket", "roblox:1+gameinfo:ticket")]
    public void CapturedLaunchPreservesTheCompleteUri(string value, string expected)
    {
        Assert.IsTrue(RobloxPlayControl.TryParseCapturedLaunchUri(value, out var actual));
        Assert.AreEqual(new Uri(expected), actual);
    }

    [TestMethod]
    [DataRow("\"https://www.roblox.com/games/123\"")]
    [DataRow("\"javascript:alert(1)\"")]
    [DataRow("\"roblox-player:bad\\x\"")]
    [DataRow("")]
    [DataRow(null)]
    public void InvalidCaptureDoesNotReturnALaunchUri(string? value)
    {
        Assert.IsFalse(RobloxPlayControl.TryParseCapturedLaunchUri(value, out var actual));
        Assert.IsNull(actual);
    }
}

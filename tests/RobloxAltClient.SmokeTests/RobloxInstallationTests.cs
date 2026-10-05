using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAltClient.Services;

namespace RobloxAltClient.SmokeTests;

[TestClass]
public sealed class RobloxInstallationTests
{
    [TestMethod]
    public void StandardUsesUpdatedMachineRegistrationInsteadOfOldUserInstallation()
    {
        WithInstallations((userRoot, machineRoot, oldPlayer, currentPlayer) =>
        {
            var selected = RobloxLauncherService.FindStandardRoblox(
                [userRoot, machineRoot], [$"\"{oldPlayer}\" %1", $"\"{currentPlayer}\" %1"]);
            Assert.AreEqual(currentPlayer, selected,
                "Starting the stale AppData player forces the updater to run again for every account.");
        });
    }

    [TestMethod]
    public void ActiveRegistrationWinsOverAnUnregisteredLeftover()
    {
        WithInstallations((userRoot, machineRoot, oldPlayer, currentPlayer) =>
        {
            File.SetLastWriteTimeUtc(oldPlayer, DateTime.UtcNow.AddDays(1));
            var selected = RobloxLauncherService.FindStandardRoblox(
                [userRoot, machineRoot], [$"\"{currentPlayer}\" %1"]);
            Assert.AreEqual(currentPlayer, selected);
        });
    }

    [TestMethod]
    public void MissingOrForeignRegistrationsFallBackAcrossBothInstallationLocations()
    {
        WithInstallations((userRoot, machineRoot, oldPlayer, currentPlayer) =>
        {
            var foreignPlayer = Path.Combine(Path.GetDirectoryName(userRoot)!, "RobloxPlayerBeta.exe");
            File.WriteAllText(foreignPlayer, "foreign");
            var selected = RobloxLauncherService.FindStandardRoblox(
                [userRoot, machineRoot, Path.Combine(machineRoot, "missing")],
                [$"\"{foreignPlayer}\" %1", $"\"{machineRoot}\\gone\\RobloxPlayerBeta.exe\" %1"]);
            Assert.AreEqual(currentPlayer, selected);
        });
    }

    [TestMethod]
    public void EmptyInstallationReturnsNull()
    {
        Assert.IsNull(RobloxLauncherService.FindStandardRoblox(
            [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], []));
    }

    private static void WithInstallations(Action<string, string, string, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-installation-" + Guid.NewGuid().ToString("N"));
        var userRoot = Path.Combine(root, "user", "Versions");
        var machineRoot = Path.Combine(root, "machine", "Versions");
        var oldPlayer = Path.Combine(userRoot, "version-old", "RobloxPlayerBeta.exe");
        var currentPlayer = Path.Combine(machineRoot, "version-current", "RobloxPlayerBeta.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPlayer)!);
        Directory.CreateDirectory(Path.GetDirectoryName(currentPlayer)!);
        try
        {
            File.WriteAllText(oldPlayer, "old");
            File.WriteAllText(currentPlayer, "current");
            File.SetLastWriteTimeUtc(oldPlayer, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(currentPlayer, DateTime.UtcNow.AddDays(-1));
            test(userRoot, machineRoot, oldPlayer, currentPlayer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.PluginSdk;
using RobloxAltClient.Services;

namespace RobloxAltClient.SmokeTests;

[TestClass]
public sealed class RobloxStartupTests
{
    [TestMethod]
    public void UpdateHandoffDoesNotAcceptTheTemporaryClient()
    {
        var tracker = new RobloxStartupTracker<(int Pid, long Start)>();
        var updatingClient = (100, 1L);
        Assert.IsFalse(tracker.Observe(updatingClient, TimeSpan.Zero));
        Assert.IsFalse(tracker.Observe(updatingClient, TimeSpan.FromMilliseconds(300)));
        Assert.IsFalse(tracker.Observe(updatingClient, TimeSpan.FromMilliseconds(600)),
            "Three process polls do not establish startup stability.");
        Assert.IsFalse(tracker.Observe(updatingClient, TimeSpan.FromSeconds(4)));
        Assert.IsFalse(tracker.Observe(null, TimeSpan.FromSeconds(5)));

        var replacement = (101, 2L);
        Assert.IsFalse(tracker.Observe(replacement, TimeSpan.FromSeconds(10)));
        Assert.IsFalse(tracker.Observe(replacement, TimeSpan.FromSeconds(17.9)));
        Assert.IsTrue(tracker.Observe(replacement, TimeSpan.FromSeconds(18)),
            "The replacement must be accepted after its own startup interval.");
    }

    [TestMethod]
    public void ReusedPidAndAmbiguousCandidatesResetStartupStability()
    {
        var tracker = new RobloxStartupTracker<(int Pid, long Start)>();
        Assert.IsFalse(tracker.Observe((100, 1), TimeSpan.Zero));
        Assert.IsFalse(tracker.Observe((100, 2), TimeSpan.FromSeconds(7)));
        // null represents either no candidate or more than one candidate.
        Assert.IsFalse(tracker.Observe(null, TimeSpan.FromSeconds(14)));
        Assert.IsFalse(tracker.Observe((100, 2), TimeSpan.FromSeconds(15)));
        Assert.IsFalse(tracker.Observe((100, 2), TimeSpan.FromSeconds(22.9)));
        Assert.IsTrue(tracker.Observe((100, 2), TimeSpan.FromSeconds(23)));
    }

    [TestMethod]
    public void ExecutableChangeResetsStartupStability()
    {
        var tracker = new RobloxStartupTracker<(int Pid, long Start, string Executable)>();
        Assert.IsFalse(tracker.Observe((100, 1, @"C:\Roblox\old\RobloxPlayerBeta.exe"), TimeSpan.Zero));
        var replacement = (100, 1L, @"C:\Roblox\new\RobloxPlayerBeta.exe");
        Assert.IsFalse(tracker.Observe(replacement, TimeSpan.FromSeconds(7)));
        Assert.IsFalse(tracker.Observe(replacement, TimeSpan.FromSeconds(14.9)));
        Assert.IsTrue(tracker.Observe(replacement, TimeSpan.FromSeconds(15)));
    }

    [TestMethod]
    [DataRow("[FLog::UpdateController] updateRequired TRUE")]
    [DataRow("[FLog::UpdateController] Update mode is chosen as FORCE")]
    public void EitherUpdateMarkerReportsAnUpdateWithoutClaimingWhoKilledTheClient(string updateLine)
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new DateTime(2026, 10, 4, 19, 25, 27, DateTimeKind.Utc);
            File.WriteAllText(Path.Combine(root, $"0.741.0.7411058_{start:yyyyMMddTHHmmss}Z_Player_587CD_last.log"), updateLine);
            var snapshot = new ManagedAccountSnapshot("test", "Test", 100, start.Ticks,
                nint.Zero, 0, 0, 100, 100, 96, false, start, false);
            var lines = RobloxLogAutopsy.Autopsy(snapshot, root);
            Assert.IsTrue(lines.Any(line => line.Contains("REQUIRED or forced update", StringComparison.Ordinal)));
            Assert.IsFalse(lines.Any(line => line.Contains("launcher did not kill", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ProductionLogDoesNotRecommendLeavingATestProgram()
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new DateTime(2026, 10, 4, 19, 31, 1, DateTimeKind.Utc);
            File.WriteAllLines(Path.Combine(root, $"0.741.0.7411058_{start:yyyyMMddTHHmmss}Z_Player_7930D_last.log"),
            [
                "[FLog::CrashpadHandlerLog] Crashpad handler logging initialized",
                "[FLog::Output] RobloxChannel has been set to production"
            ]);
            var snapshot = new ManagedAccountSnapshot("test", "Test", 100, start.AddMilliseconds(750).Ticks,
                nint.Zero, 0, 0, 100, 100, 96, false, start, false);
            var lines = RobloxLogAutopsy.Autopsy(snapshot, root);
            Assert.IsTrue(lines.Any(line => line == "Roblox channel: production."));
            Assert.IsFalse(lines.Any(line => line.Contains("opt out", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(lines.Any(line => line.Contains("forced", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(22)]
    [DataRow(4)]
    [DataRow(2)]
    public void MissingStartupLogDoesNotBorrowThePreviousAccountsUpdate(int gapSeconds)
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-neighbor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var previousStart = new DateTime(2026, 10, 5, 6, 34, 31, DateTimeKind.Utc);
            File.WriteAllText(Path.Combine(root, $"0.741.0.7411058_{previousStart:yyyyMMddTHHmmss}Z_Player_53EE2_last.log"),
                "[FLog::UpdateController] updateRequired TRUE");
            var currentStart = previousStart.AddSeconds(gapSeconds);
            var snapshot = new ManagedAccountSnapshot("four", "Fourth", 200, currentStart.Ticks,
                nint.Zero, 0, 0, 100, 100, 96, false, currentStart, false);
            var lines = RobloxLogAutopsy.Autopsy(snapshot, root);
            Assert.IsTrue(lines.Single().Contains("No Roblox session log", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(6)]
    public void LogCreatedDuringStartupIsMatched(int delaySeconds)
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-delayed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var processStart = new DateTime(2026, 10, 5, 6, 35, 10, 400, DateTimeKind.Utc);
            var logStart = processStart.AddSeconds(delaySeconds);
            File.WriteAllText(Path.Combine(root, $"0.741.0.7411058_{logStart:yyyyMMddTHHmmss}Z_Player_9C1F0_last.log"),
                "[FLog::UpdateController] updateRequired TRUE");
            var snapshot = new ManagedAccountSnapshot("four", "Fourth", 200, processStart.Ticks,
                nint.Zero, 0, 0, 100, 100, 96, false, processStart, false);
            var lines = RobloxLogAutopsy.Autopsy(snapshot, root);
            Assert.IsTrue(lines.Any(line => line.Contains("REQUIRED or forced update", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SameSecondSessionLogsAreReportedAsUnmatched()
    {
        var root = Path.Combine(Path.GetTempPath(), "Roblox-ambiguous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new DateTime(2026, 10, 5, 6, 34, 53, DateTimeKind.Utc);
            foreach (var id in new[] { "ABCDE", "12345" })
                File.WriteAllText(Path.Combine(root, $"0.741.0.7411058_{start:yyyyMMddTHHmmss}Z_Player_{id}_last.log"),
                    "[FLog::UpdateController] updateRequired TRUE");
            var snapshot = new ManagedAccountSnapshot("four", "Fourth", 200, start.Ticks,
                nint.Zero, 0, 0, 100, 100, 96, false, start, false);
            var lines = RobloxLogAutopsy.Autopsy(snapshot, root);
            Assert.IsTrue(lines.Single().Contains("No Roblox session log", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

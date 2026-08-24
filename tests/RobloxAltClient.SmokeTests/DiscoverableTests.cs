using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.TestInfrastructure;

namespace RobloxAltClient.SmokeTests;

[TestClass]
public sealed class DiscoverableTests
{
    [TestMethod]
    public Task WindowsSmokeScenariosPass() =>
        ExecutableScenarioRunner.RunAsync(typeof(DiscoverableTests).Assembly, TimeSpan.FromMinutes(10));
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.TestInfrastructure;

namespace RobloxAccountManager.Platform.MacOS.Tests;

[TestClass]
public sealed class DiscoverableTests
{
    [TestMethod]
    public Task MacPlatformSafetyScenariosPass() =>
        ExecutableScenarioRunner.RunAsync(typeof(DiscoverableTests).Assembly, TimeSpan.FromMinutes(10));
}

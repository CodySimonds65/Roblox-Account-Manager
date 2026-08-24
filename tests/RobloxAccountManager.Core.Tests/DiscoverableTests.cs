using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.TestInfrastructure;

namespace RobloxAccountManager.Core.Tests;

[TestClass]
public sealed class DiscoverableTests
{
    [TestMethod]
    public Task CoreSecurityAndLaunchScenariosPass() =>
        ExecutableScenarioRunner.RunAsync(typeof(DiscoverableTests).Assembly, TimeSpan.FromMinutes(3));
}

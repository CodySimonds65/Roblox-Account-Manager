using Microsoft.VisualStudio.TestTools.UnitTesting;
using RobloxAccountManager.TestInfrastructure;

namespace RobloxAccountManager.Desktop.Tests;

[TestClass]
public sealed class DiscoverableTests
{
    [TestMethod]
    public Task DesktopPolicyScenariosPass() =>
        ExecutableScenarioRunner.RunAsync(typeof(DiscoverableTests).Assembly, TimeSpan.FromMinutes(3));
}

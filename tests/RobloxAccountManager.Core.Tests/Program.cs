using System.Text.Json;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Core.Navigation;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var gate = new RobloxNavigationGate();
gate.CommitTopLevelNavigation(new Uri("https://www.roblox.com/games/123/example"), succeeded: true);
Require(gate.TryBeginLaunch(), "The first browser launch did not enter pending state.");
var ticketUri = new Uri("roblox-player:1+launchmode:play+gameinfo:secret-ticket");
var navigation = gate.Evaluate(ticketUri);
Require(navigation.Accepted && navigation.TryConsumeLaunchUri(out var consumed) && consumed == ticketUri,
    "A trusted pending launch was not consumed.");
Require(!navigation.TryConsumeLaunchUri(out _), "A ticket URI was consumable more than once.");
Require(!gate.Evaluate(ticketUri).Accepted, "The navigation gate accepted a second URI without a pending launch.");
Require(!navigation.ToString().Contains("secret-ticket", StringComparison.Ordinal),
    "BrowserNavigationResult.ToString leaked an authentication ticket.");
Require(!JsonSerializer.Serialize(navigation).Contains("secret-ticket", StringComparison.Ordinal),
    "BrowserNavigationResult JSON leaked an authentication ticket.");

gate.CommitTopLevelNavigation(new Uri("https://roblox.com.evil.example/games/1"), succeeded: true);
Require(gate.TryBeginLaunch(), "The pending launch could not be reset after consumption.");
Require(!gate.Evaluate(ticketUri).Accepted, "A lookalike Roblox hostname was trusted.");
gate.CancelPendingLaunch();

var connection = new PluginConnectionRequest("plugin.id", "/tmp/ram.sock", "secret-token", TimeSpan.FromSeconds(5));
Require(!connection.ToString().Contains("secret-token", StringComparison.Ordinal), "Plugin request ToString leaked its token.");
Require(!JsonSerializer.Serialize(connection).Contains("secret-token", StringComparison.Ordinal), "Plugin request JSON leaked its token.");
var unconfiguredMac = new DefaultPlatformCapabilities(RobloxPlatform.MacOS);
Require(unconfiguredMac.Get(CapabilityNames.PluginHost).Status == CapabilityStatus.Disabled &&
        unconfiguredMac.Get(CapabilityNames.NativeSettings).Status == CapabilityStatus.Disabled &&
        unconfiguredMac.Get(CapabilityNames.BrowserProfileDeletion).Status == CapabilityStatus.Disabled,
    "Unconfigured macOS adapters were advertised as supported.");

var macUpper = new RobloxProcessIdentity(1, DateTimeOffset.UnixEpoch, "/Applications/Roblox.app/Contents/MacOS/Roblox", "/Applications/Roblox.app", RobloxPlatform.MacOS);
var macLower = macUpper with { ExecutablePath = "/applications/Roblox.app/Contents/MacOS/Roblox" };
Require(!macUpper.Matches(macLower), "macOS identity paths were compared case-insensitively.");
var windowsUpper = macUpper with { Platform = RobloxPlatform.Windows, ExecutablePath = "C:\\Roblox\\Player.exe", BundlePath = null };
var windowsLower = windowsUpper with { ExecutablePath = "c:\\roblox\\player.exe" };
Require(windowsUpper.Matches(windowsLower), "Windows identity paths were not compared case-insensitively.");

var locator = new RetryLocator();
var strategy = new SuccessfulStrategy();
var launcher = new RecordingLauncher();
var coordinator = new SerializedLaunchCoordinator(locator, strategy, launcher);
var uriCalls = 0;
var result = await coordinator.LaunchAsync(new RobloxLaunchRequest(
    "account",
    _ => ValueTask.FromResult(new Uri($"roblox-player:1+gameinfo:ticket-{++uriCalls}")),
    MaxAttempts: 2));
Require(result.Succeeded, "The retry coordinator did not return the verified second process.");
Require(uriCalls == 2 && launcher.LaunchCount == 2, "A retry reused a launch URI or skipped an attempt.");
Require(locator.SnapshotCount == 2, "The process baseline was not refreshed before every launch attempt.");

Console.WriteLine("Core security and launch-coordination tests passed.");

sealed class RetryLocator : IRobloxProcessLocator
{
    private int _verificationCount;
    public int SnapshotCount { get; private set; }

    public ValueTask<RobloxLaunchSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        SnapshotCount++;
        return ValueTask.FromResult(new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, []));
    }

    public ValueTask<LaunchVerificationResult> VerifyLaunchedProcessAsync(
        RobloxLaunchSnapshot before, RobloxLaunchRequest request, CancellationToken cancellationToken = default)
    {
        _verificationCount++;
        if (_verificationCount == 1)
            return ValueTask.FromResult(LaunchVerificationResult.Failure(LaunchFailureKind.ProcessNotFound, "not-found"));
        var identity = new RobloxProcessIdentity(42, DateTimeOffset.UtcNow, "/Applications/Roblox.app/Contents/MacOS/Roblox", "/Applications/Roblox.app", RobloxPlatform.MacOS);
        return ValueTask.FromResult(new LaunchVerificationResult(true, new RobloxProcessInfo(identity, true, request.AccountId)));
    }

    public ValueTask<IReadOnlyList<RobloxProcessInfo>> GetManagedProcessesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<RobloxProcessInfo>>([]);
}

sealed class SuccessfulStrategy : IRobloxMultiInstanceStrategy
{
    public RobloxPlatform Platform => RobloxPlatform.MacOS;
    public ValueTask PrepareAsync(RobloxLaunchRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<SingletonReleaseResult> ReleaseSingletonAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SingletonReleaseResult(SingletonReleaseStatus.Released));
    public ValueTask<MacLaunchLevel?> GetActiveMacLevelAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MacLaunchLevel?>(MacLaunchLevel.OriginalBundle);
}

sealed class RecordingLauncher : IRobloxPlatformLauncher
{
    public RobloxPlatform Platform => RobloxPlatform.MacOS;
    public int LaunchCount { get; private set; }
    public ValueTask<PlatformLaunchResult> LaunchAsync(RobloxLaunchRequest request, Uri freshLaunchUri, CancellationToken cancellationToken = default)
    {
        LaunchCount++;
        return ValueTask.FromResult(PlatformLaunchResult.Success());
    }
}

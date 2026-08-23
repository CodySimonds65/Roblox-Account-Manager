using System.Text.Json;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Core.Navigation;
using RobloxAccountManager.Core.Data;
using RobloxAccountManager.Core.Models;

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

var launchEvents = new List<string>();
var locator = new RetryLocator(launchEvents);
var strategy = new SuccessfulStrategy(launchEvents);
var launcher = new RecordingLauncher(launchEvents);
var coordinator = new SerializedLaunchCoordinator(locator, strategy, launcher);
var uriCalls = 0;
var result = await coordinator.LaunchAsync(new RobloxLaunchRequest(
    "account",
    _ =>
    {
        launchEvents.Add("ticket");
        return ValueTask.FromResult(new Uri($"roblox-player:1+gameinfo:ticket-{++uriCalls}"));
    },
    MaxAttempts: 2));
Require(result.Succeeded, "The retry coordinator did not return the verified second process.");
Require(uriCalls == 2 && launcher.LaunchCount == 2, "A retry reused a launch URI or skipped an attempt.");
Require(locator.SnapshotCount == 2, "The process baseline was not refreshed before every launch attempt.");
Require(strategy.DisposedLeaseCount == 2, "Prepared launch slot leases were not released after coordination completed.");
Require(launcher.BundlePaths.All(path => path.Contains("prepared-slot-", StringComparison.Ordinal)),
    "The platform launcher did not receive the effective prepared bundle path.");
Require(string.Join(",", launchEvents.Take(6)) == "prepare,snapshot,release,ticket,launch,verify",
    "Launch preparation did not complete before snapshot, singleton release, and ticket acquisition.");
Require(locator.ValidatedFingerprints.Count == 2
        && locator.ValidatedFingerprints.All(fingerprint => fingerprint == "pre-launch-fingerprint"),
    "The pre-launch bundle fingerprint was not carried into every process verification attempt.");

var rejectedTicketCalls = 0;
var rejectedLocator = new RetryLocator();
var rejected = await new SerializedLaunchCoordinator(
        rejectedLocator,
        new RejectingStrategy(),
        new RecordingLauncher())
    .LaunchAsync(new RobloxLaunchRequest(
        "account",
        _ =>
        {
            rejectedTicketCalls++;
            return ValueTask.FromResult(ticketUri);
        },
        MaxAttempts: 3));
Require(!rejected.Succeeded
        && rejected.FailureKind == LaunchFailureKind.LauncherRejected
        && rejected.Attempts.Count == 1
        && rejected.Attempts[0].DiagnosticCode == "consent-required"
        && rejectedTicketCalls == 0
        && rejectedLocator.SnapshotCount == 0,
    "A non-retryable preparation failure reached snapshot or ticket acquisition.");

Require(GamePreset.TryNormalizeRobloxGameUrl("https://www.roblox.com/games/123/example", out var normalized)
        && normalized.Contains("/games/123/", StringComparison.Ordinal),
    "A valid Roblox game URL was not normalized.");
Require(!GamePreset.TryNormalizeRobloxGameUrl("https://roblox.com.evil.example/games/123", out _),
    "A lookalike Roblox game URL was accepted.");
const string privateServerShare = "https://www.roblox.com/share?code=b5f0d0b82d5a53419841df9f978bed53&type=Server";
Require(GamePreset.TryNormalizeRobloxGameUrl(privateServerShare, out var normalizedPrivateServer)
        && normalizedPrivateServer == privateServerShare,
    "A Roblox private server share URL was rejected or changed.");
Require(GamePreset.TryNormalizeRobloxGameUrl(
            "https://roblox.com/share?code=share-code%2Bwith%2Fsymbols&type=server&source=invite",
            out var canonicalPrivateServer)
        && canonicalPrivateServer == "https://www.roblox.com/share?code=share-code%2Bwith%2Fsymbols&type=server&source=invite",
    "A bare-host private server share URL was not canonicalized while preserving its query.");
Require(!GamePreset.TryNormalizeRobloxGameUrl("http://www.roblox.com/share?code=secret&type=Server", out _),
    "An HTTP private server share URL was accepted.");
Require(!GamePreset.TryNormalizeRobloxGameUrl("https://www.roblox.com/share?type=Server", out _),
    "A private server share URL without a code was accepted.");
Require(!GamePreset.TryNormalizeRobloxGameUrl("https://www.roblox.com/share?code=secret&type=Experience", out _),
    "A non-server Roblox share URL was accepted as a private server link.");
var resolvedSettings = GameSettings.Resolve(
    new GameSettings { GraphicsQuality = 3, FpsLimit = 60 },
    new GameSettings { GraphicsQuality = 6 },
    new GameSettings { MasterVolumeLevel = 4 });
Require(resolvedSettings.GraphicsQuality == 6 && resolvedSettings.FpsLimit == 60 && resolvedSettings.MasterVolumeLevel == 4,
    "Scoped Roblox settings did not resolve profile over game over global values.");

var storeRoot = Path.Combine(Path.GetTempPath(), "ram-core-store-" + Guid.NewGuid().ToString("N"));
try
{
    var paths = new LauncherDataPaths(storeRoot);
    var accounts = new AccountStore(paths);
    var presetStore = new GamePresetStore(paths);
    var settingsStore = new SettingsStore(paths);
    var builtIns = GamePresetStore.EnsureBuiltIns([
        new GamePreset("Duplicate Dungeon", "https://roblox.com/games/77649408247578/Dungeon-Quest-Reborn"),
        new GamePreset("Custom game", "https://www.roblox.com/games/123/custom")]);
    Require(builtIns.Count == 3 && builtIns.Count(x => x.IsBuiltIn) == 2
            && builtIns[0].Name == "Dungeon Quest Reborn"
            && GamePresetStore.EnsureBuiltIns(builtIns).Count == 3,
        "Built-in presets were not inserted idempotently or deduplicated by URL.");
    await presetStore.SaveAsync(builtIns);
    var persistedPresets = await presetStore.LoadAsync();
    Require(persistedPresets.Count == 1 && persistedPresets[0].Name == "Custom game",
        "Built-in presets were persisted instead of remaining runtime-only.");
    Require(new LauncherSettings().UpdateChannel == UpdateChannel.Signed && new LauncherSettings().ShowGamePresetPanel,
        "The default update channel or game preset visibility was incorrect.");
    await settingsStore.SaveAsync(new LauncherSettings { UpdateChannel = UpdateChannel.Unsigned, ShowGamePresetPanel = false });
    var roundTrippedSettings = await settingsStore.LoadAsync();
    Require(roundTrippedSettings.UpdateChannel == UpdateChannel.Unsigned && !roundTrippedSettings.ShowGamePresetPanel,
        "The update channel or game preset visibility did not survive a settings-file round trip.");
    await accounts.SaveAsync([new AccountProfile { Id = Guid.NewGuid().ToString("N"), Label = "Imported" }]);
    var loadedAccounts = await accounts.LoadAsync();
    Require(loadedAccounts.Count == 1 && loadedAccounts[0].Label == "Imported", "Portable account storage did not round-trip.");

    var exportPath = Path.Combine(storeRoot, "profile-export.json");
    var transferSettings = new LauncherSettings
    {
        MultiInstanceConsentGranted = true,
        RobloxSettingsConsentGranted = true,
        UnsignedUpdatesConsentGranted = true,
        UpdateChannel = UpdateChannel.Unsigned,
        ShowGamePresetPanel = false,
        ClearBrowserDataOnNextStart = true
    };
    await ProfileTransferService.ExportAsync(
        exportPath,
        loadedAccounts,
        [new GamePreset("Test", "https://www.roblox.com/games/123/test")],
        transferSettings);
    var imported = await ProfileTransferService.ImportAsync(exportPath);
    Require(!imported.Settings.MultiInstanceConsentGranted
            && !imported.Settings.RobloxSettingsConsentGranted
            && !imported.Settings.UnsignedUpdatesConsentGranted
            && !imported.Settings.ClearBrowserDataOnNextStart
            && !imported.Settings.ShowGamePresetPanel
            && imported.Settings.UpdateChannel == UpdateChannel.Unsigned,
        "Profile import carried sensitive local consent into the current installation.");
}
finally
{
    if (Directory.Exists(storeRoot)) Directory.Delete(storeRoot, recursive: true);
}

Console.WriteLine("Core security and launch-coordination tests passed.");

sealed class RetryLocator(List<string>? events = null) : IRobloxProcessLocator
{
    private int _verificationCount;
    public int SnapshotCount { get; private set; }
    public List<string?> ValidatedFingerprints { get; } = [];

    public ValueTask<RobloxLaunchSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        events?.Add("snapshot");
        SnapshotCount++;
        return ValueTask.FromResult(new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, []));
    }

    public ValueTask<LaunchVerificationResult> VerifyLaunchedProcessAsync(
        RobloxLaunchSnapshot before, RobloxLaunchRequest request, CancellationToken cancellationToken = default)
    {
        events?.Add("verify");
        ValidatedFingerprints.Add(request.ValidatedRobloxBundleFingerprint);
        _verificationCount++;
        if (_verificationCount == 1)
            return ValueTask.FromResult(LaunchVerificationResult.Failure(LaunchFailureKind.ProcessNotFound, "not-found"));
        var identity = new RobloxProcessIdentity(42, DateTimeOffset.UtcNow, "/Applications/Roblox.app/Contents/MacOS/Roblox", "/Applications/Roblox.app", RobloxPlatform.MacOS);
        return ValueTask.FromResult(new LaunchVerificationResult(true, new RobloxProcessInfo(identity, true, request.AccountId)));
    }

    public ValueTask<IReadOnlyList<RobloxProcessInfo>> GetManagedProcessesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<RobloxProcessInfo>>([]);
}

sealed class SuccessfulStrategy(List<string>? events = null) : IRobloxMultiInstanceStrategy
{
    private int _preparationCount;
    public int DisposedLeaseCount { get; private set; }
    public RobloxPlatform Platform => RobloxPlatform.MacOS;
    public ValueTask<RobloxLaunchPreparation> PrepareAsync(
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        events?.Add("prepare");
        var slot = ++_preparationCount;
        var prepared = request with
        {
            RobloxBundlePath = $"/prepared-slot-{slot}/Roblox.app",
            PreferredMacLevel = MacLaunchLevel.ManagedSlots
        };
        return ValueTask.FromResult(RobloxLaunchPreparation.Success(
            prepared,
            MacLaunchLevel.ManagedSlots,
            new CallbackLease(() => DisposedLeaseCount++)));
    }
    public ValueTask<SingletonReleaseResult> ReleaseSingletonAsync(CancellationToken cancellationToken = default) =>
        RecordRelease();
    public ValueTask<MacLaunchLevel?> GetActiveMacLevelAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MacLaunchLevel?>(MacLaunchLevel.ManagedSlots);

    private ValueTask<SingletonReleaseResult> RecordRelease()
    {
        events?.Add("release");
        return ValueTask.FromResult(new SingletonReleaseResult(SingletonReleaseStatus.Released));
    }

    private sealed class CallbackLease(Action dispose) : IAsyncDisposable
    {
        private Action? _dispose = dispose;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}

sealed class RecordingLauncher(List<string>? events = null) : IRobloxPlatformLauncher
{
    public RobloxPlatform Platform => RobloxPlatform.MacOS;
    public int LaunchCount { get; private set; }
    public List<string> BundlePaths { get; } = [];
    public ValueTask<PlatformLaunchResult> LaunchAsync(RobloxLaunchRequest request, Uri freshLaunchUri, CancellationToken cancellationToken = default)
    {
        events?.Add("launch");
        LaunchCount++;
        BundlePaths.Add(request.RobloxBundlePath ?? string.Empty);
        return ValueTask.FromResult(PlatformLaunchResult.Success("pre-launch-fingerprint"));
    }
}

sealed class RejectingStrategy : IRobloxMultiInstanceStrategy
{
    public RobloxPlatform Platform => RobloxPlatform.MacOS;
    public ValueTask<RobloxLaunchPreparation> PrepareAsync(
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RobloxLaunchPreparation.Failure(
            request,
            LaunchFailureKind.LauncherRejected,
            "consent-required"));
    public ValueTask<SingletonReleaseResult> ReleaseSingletonAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Singleton release must not run after preparation rejection.");
    public ValueTask<MacLaunchLevel?> GetActiveMacLevelAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<MacLaunchLevel?>(MacLaunchLevel.ManagedSlots);
}

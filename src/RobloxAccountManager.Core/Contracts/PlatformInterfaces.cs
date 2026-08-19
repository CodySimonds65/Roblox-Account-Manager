namespace RobloxAccountManager.Core.Contracts;

public interface IAccountBrowserSessionService
{
    ValueTask<BrowserSessionDescriptor> CreateAsync(string accountId, string profileName, CancellationToken cancellationToken = default);
    ValueTask<BrowserNavigationResult> NavigateAsync(string accountId, Uri navigationUri, CancellationToken cancellationToken = default);
    ValueTask RemoveAsync(string accountId, CancellationToken cancellationToken = default);
    ValueTask DisposeAsync(string accountId, CancellationToken cancellationToken = default);
}

public interface IAccountBrowserDataStoreRemover
{
    bool IsSupported { get; }
    ValueTask RemoveAsync(Guid dataStoreIdentifier, CancellationToken cancellationToken = default);
}

public interface IRobloxProcessLocator
{
    ValueTask<RobloxLaunchSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<LaunchVerificationResult> VerifyLaunchedProcessAsync(
        RobloxLaunchSnapshot before,
        RobloxLaunchRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<RobloxProcessInfo>> GetManagedProcessesAsync(CancellationToken cancellationToken = default);
}

public interface IRobloxMultiInstanceStrategy
{
    RobloxPlatform Platform { get; }
    ValueTask PrepareAsync(RobloxLaunchRequest request, CancellationToken cancellationToken = default);
    ValueTask<SingletonReleaseResult> ReleaseSingletonAsync(CancellationToken cancellationToken = default);
    ValueTask<MacLaunchLevel?> GetActiveMacLevelAsync(CancellationToken cancellationToken = default);
}

public interface IRobloxPlatformLauncher
{
    RobloxPlatform Platform { get; }
    ValueTask<PlatformLaunchResult> LaunchAsync(
        RobloxLaunchRequest request,
        Uri freshLaunchUri,
        CancellationToken cancellationToken = default);
}

public interface IClientWindowManager
{
    ValueTask<IReadOnlyList<RobloxWindowInfo>> GetWindowsAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> FocusAsync(RobloxWindowInfo window, CancellationToken cancellationToken = default);
    ValueTask<bool> TileAsync(IReadOnlyList<RobloxWindowInfo> windows, CancellationToken cancellationToken = default);
    ValueTask CloseAsync(RobloxProcessInfo process, CancellationToken cancellationToken = default);
}

public interface IPluginTransport : IAsyncDisposable
{
    ValueTask ConnectAsync(PluginConnectionRequest request, CancellationToken cancellationToken = default);
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}

public interface IPluginProcessSupervisor : IAsyncDisposable
{
    ValueTask StartAsync(PluginManifest manifest, CancellationToken cancellationToken = default);
    ValueTask StopAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> GetRunningPluginIdsAsync(CancellationToken cancellationToken = default);
}

public interface IPlatformCapabilities
{
    RobloxPlatform Platform { get; }
    PlatformCapabilitySnapshot Snapshot { get; }
    CapabilityDescriptor Get(string capabilityName);
}

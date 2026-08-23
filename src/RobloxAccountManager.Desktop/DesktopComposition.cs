using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Core.Data;
using RobloxAccountManager.Desktop.Services;
using RobloxAccountManager.Platform.MacOS;

namespace RobloxAccountManager.Desktop;

public sealed record DesktopComposition(
    IPlatformCapabilities Capabilities,
    AvaloniaAccountBrowserSessionService BrowserSessions,
    SerializedLaunchCoordinator? Launches,
    RobloxAccountManager.Core.Contracts.IClientWindowManager? Clients,
    MacClientOverlayManager? ClientOverlay,
    RobloxAccountManager.Core.Contracts.IPlatformUpdateInstaller? Updates,
    RobloxAccountManager.Core.Contracts.IPlatformUpdateSource? UpdateSource,
    AccountStore Accounts,
    GamePresetStore Presets,
    SettingsStore Settings,
    RobloxAccountManager.Core.Contracts.IRobloxSettingsAdapter? RobloxSettings,
    RobloxAccountManager.Core.Contracts.IPluginHostFacade? Plugins)
{
    public static DesktopComposition Create(
        RobloxPlatform platform,
        string? trustedInstallerIdentity = null,
        string? dataRoot = null)
    {
        IAccountBrowserDataStoreRemover dataStoreRemover = platform == RobloxPlatform.MacOS
            ? new MacAccountBrowserDataStoreRemover()
            : new UnsupportedWebsiteDataStoreRemover();
        var browserSessions = new AvaloniaAccountBrowserSessionService(dataStoreRemover);
        var paths = new LauncherDataPaths(dataRoot);
        var accounts = new AccountStore(paths);
        var presets = new GamePresetStore(paths);
        var settings = new SettingsStore(paths);
        SerializedLaunchCoordinator? launches = null;
        RobloxAccountManager.Core.Contracts.IClientWindowManager? clients = null;
        MacClientOverlayManager? clientOverlay = null;
        RobloxAccountManager.Core.Contracts.IPlatformUpdateInstaller? updates = null;
        RobloxAccountManager.Core.Contracts.IPlatformUpdateSource? updateSource = null;
        RobloxAccountManager.Core.Contracts.IRobloxSettingsAdapter? robloxSettings = null;
        RobloxAccountManager.Core.Contracts.IPluginHostFacade? plugins = null;
        var accessibilityGranted = false;
        if (platform == RobloxPlatform.MacOS)
        {
            var registry = new MacManagedProcessRegistry();
            var nativeLocator = new MacRobloxProcessLocator(registry);
            var discovery = new MacBundleDiscovery();
            var runtimeRoot = MacManagedRuntimeBuilder.GetDefaultRuntimeRoot();
            var slotManager = new MacManagedRuntimeSlotManager(
                runtimeRoot,
                discovery,
                processLocator: nativeLocator);
            var coreLocator = new MacCoreProcessLocator(nativeLocator, discovery);
            launches = new SerializedLaunchCoordinator(
                coreLocator,
                new MacCoreMultiInstanceStrategy(slotManager: slotManager, bundleDiscovery: discovery),
                new MacCorePlatformLauncher(discovery, managedRuntimeRoot: runtimeRoot));
            clients = new MacCoreClientWindowManager(new MacAccessibilityWindowManager(nativeLocator), coreLocator);
            var accessibility = new MacAccessibilityApi(nativeLocator);
            accessibilityGranted = accessibility.GetCapability().IsSupported;
            clientOverlay = new MacClientOverlayManager(nativeLocator, accessibility);
            robloxSettings = new MacRobloxSettingsAdapter();
            plugins = new MacPluginHostFacade();
            updateSource = new MacGitHubReleaseUpdateSource(rid: MacPkgUpdateInstaller.GetCurrentRid());
            if (!string.IsNullOrWhiteSpace(trustedInstallerIdentity) || OperatingSystem.IsMacOS())
            {
                try
                {
                    updates = new MacPkgUpdateInstaller(
                        expectedRid: MacPkgUpdateInstaller.GetCurrentRid(),
                        trust: new MacPkgTrustConfiguration(
                            trustedInstallerIdentity ?? "unsigned-development",
                            "io.github.codysimonds65.roblox-account-manager",
                            "io.github.codysimonds65.roblox-account-manager",
                            "RobloxAccountManager",
                            AllowUnsignedPackages: true));
                }
                catch (ArgumentException)
                {
                    // Update installation remains unavailable until the signed bundle provides
                    // a complete installer identity and current numeric package version.
                }
            }
        }

        var capabilities = new DefaultPlatformCapabilities(
            platform,
            accessibilityGranted,
            pluginHostAvailable: plugins?.IsAvailable == true,
            nativeSettingsAvailable: robloxSettings is not null,
            browserProfileDeletionAvailable: dataStoreRemover.IsSupported);
        return new DesktopComposition(capabilities, browserSessions, launches, clients, clientOverlay, updates, updateSource, accounts, presets, settings, robloxSettings, plugins);
    }
}

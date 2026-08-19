using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Desktop.Services;
using RobloxAccountManager.Platform.MacOS;

namespace RobloxAccountManager.Desktop;

public sealed record DesktopComposition(
    IPlatformCapabilities Capabilities,
    AvaloniaAccountBrowserSessionService BrowserSessions,
    SerializedLaunchCoordinator? Launches,
    RobloxAccountManager.Core.Contracts.IClientWindowManager? Clients,
    RobloxAccountManager.Core.Contracts.IPlatformUpdateInstaller? Updates)
{
    public static DesktopComposition Create(
        RobloxPlatform platform,
        string? trustedRobloxTeamIdentifier = null,
        string? trustedInstallerIdentity = null)
    {
        IAccountBrowserDataStoreRemover dataStoreRemover = platform == RobloxPlatform.MacOS
            ? new MacAccountBrowserDataStoreRemover()
            : new UnsupportedWebsiteDataStoreRemover();
        var browserSessions = new AvaloniaAccountBrowserSessionService(dataStoreRemover);
        SerializedLaunchCoordinator? launches = null;
        RobloxAccountManager.Core.Contracts.IClientWindowManager? clients = null;
        RobloxAccountManager.Core.Contracts.IPlatformUpdateInstaller? updates = null;
        if (platform == RobloxPlatform.MacOS)
        {
            var registry = new MacManagedProcessRegistry();
            var nativeLocator = new MacRobloxProcessLocator(registry);
            var coreLocator = new MacCoreProcessLocator(nativeLocator);
            if (!string.IsNullOrWhiteSpace(trustedRobloxTeamIdentifier))
            {
                var discovery = new MacBundleDiscovery(requiredTeamIdentifier: trustedRobloxTeamIdentifier);
                if (discovery.HasTrustedTeamIdentifier)
                {
                    launches = new SerializedLaunchCoordinator(
                        coreLocator,
                        new MacCoreMultiInstanceStrategy(),
                        new MacCorePlatformLauncher(discovery));
                }
            }
            clients = new MacCoreClientWindowManager(new MacAccessibilityWindowManager(nativeLocator), coreLocator);
            if (!string.IsNullOrWhiteSpace(trustedInstallerIdentity))
            {
                try
                {
                    updates = new MacPkgUpdateInstaller(
                        expectedRid: MacPkgUpdateInstaller.GetCurrentRid(),
                        trust: new MacPkgTrustConfiguration(
                            trustedInstallerIdentity,
                            "io.github.codysimonds65.roblox-account-manager",
                            "io.github.codysimonds65.roblox-account-manager",
                            "RobloxAccountManager"));
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
            browserProfileDeletionAvailable: dataStoreRemover.IsSupported);
        return new DesktopComposition(capabilities, browserSessions, launches, clients, updates);
    }
}

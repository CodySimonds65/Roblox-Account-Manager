using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Core.Capabilities;

public static class CapabilityNames
{
    public const string EmbeddedRobloxWindow = "embedded-roblox-window";
    public const string ExternalRobloxWindow = "external-roblox-window";
    public const string InputAutomation = "input-automation";
    public const string ScreenReading = "screen-reading";
    public const string GlobalInput = "global-input";
    public const string NativeSettings = "native-settings";
    public const string PluginHost = "plugin-host";
    public const string BrowserProfileDeletion = "browser-profile-deletion";
}

public sealed class DefaultPlatformCapabilities : IPlatformCapabilities
{
    public DefaultPlatformCapabilities(
        RobloxPlatform platform,
        bool accessibilityGranted = false,
        bool pluginHostAvailable = false,
        bool nativeSettingsAvailable = false,
        bool browserProfileDeletionAvailable = false)
    {
        Platform = platform;
        Snapshot = CreateSnapshot(platform, accessibilityGranted, pluginHostAvailable, nativeSettingsAvailable, browserProfileDeletionAvailable);
    }

    public RobloxPlatform Platform { get; }
    public PlatformCapabilitySnapshot Snapshot { get; }

    public CapabilityDescriptor Get(string capabilityName) => Snapshot[capabilityName];

    public static PlatformCapabilitySnapshot CreateSnapshot(
        RobloxPlatform platform,
        bool accessibilityGranted = false,
        bool pluginHostAvailable = false,
        bool nativeSettingsAvailable = false,
        bool browserProfileDeletionAvailable = false)
    {
        var capabilities = new List<CapabilityDescriptor>
        {
            new(CapabilityNames.PluginHost,
                pluginHostAvailable ? CapabilityStatus.Supported : CapabilityStatus.Disabled,
                pluginHostAvailable ? "Plugin lifecycle and account events are available." : "The plugin host adapter is not configured.",
                pluginHostAvailable ? null : "capability-not-configured"),
            new(CapabilityNames.NativeSettings,
                nativeSettingsAvailable ? CapabilityStatus.Supported : CapabilityStatus.Disabled,
                nativeSettingsAvailable ? "Platform settings can be applied through the registered adapter." : "The native settings adapter is not configured.",
                nativeSettingsAvailable ? null : "capability-not-configured"),
            new(CapabilityNames.BrowserProfileDeletion,
                browserProfileDeletionAvailable ? CapabilityStatus.Supported : CapabilityStatus.Disabled,
                browserProfileDeletionAvailable ? "Exact per-account browser store deletion is available." : "Exact per-account browser store deletion is not configured; account removal is disabled.",
                browserProfileDeletionAvailable ? null : "capability-not-configured")
        };

        if (platform == RobloxPlatform.MacOS)
        {
            capabilities.Add(new(CapabilityNames.EmbeddedRobloxWindow, CapabilityStatus.Unsupported, "Roblox runs in normal external macOS windows.", "platform-not-supported"));
            capabilities.Add(new(CapabilityNames.ExternalRobloxWindow, accessibilityGranted ? CapabilityStatus.Supported : CapabilityStatus.RequiresPermission, accessibilityGranted ? "External Roblox windows can be focused and tiled." : "Grant Accessibility permission to focus or tile external Roblox windows.", accessibilityGranted ? null : "accessibility-permission-required"));
            capabilities.Add(new(CapabilityNames.InputAutomation, CapabilityStatus.Unsupported, "Synthetic input is intentionally unavailable on macOS.", "platform-not-supported"));
            capabilities.Add(new(CapabilityNames.ScreenReading, CapabilityStatus.Unsupported, "Screen reading is not implemented on macOS.", "platform-not-supported"));
            capabilities.Add(new(CapabilityNames.GlobalInput, CapabilityStatus.Unsupported, "Global input is not implemented on macOS.", "platform-not-supported"));
        }
        else if (platform == RobloxPlatform.Windows)
        {
            capabilities.Add(new(CapabilityNames.EmbeddedRobloxWindow, CapabilityStatus.Supported, "Roblox embedding is provided by the Windows platform adapter."));
            capabilities.Add(new(CapabilityNames.ExternalRobloxWindow, CapabilityStatus.Supported, "Roblox windows can be discovered and managed."));
            capabilities.Add(new(CapabilityNames.InputAutomation, CapabilityStatus.Supported, "Windows native input is available to authorized plugins."));
            capabilities.Add(new(CapabilityNames.ScreenReading, CapabilityStatus.Supported, "Windows screen reading is available to authorized plugins."));
            capabilities.Add(new(CapabilityNames.GlobalInput, CapabilityStatus.Supported, "Windows global input is available to authorized plugins."));
        }
        else
        {
            foreach (var name in new[] { CapabilityNames.EmbeddedRobloxWindow, CapabilityNames.ExternalRobloxWindow, CapabilityNames.InputAutomation, CapabilityNames.ScreenReading, CapabilityNames.GlobalInput })
                capabilities.Add(new(name, CapabilityStatus.Unsupported, "This platform is not supported.", "platform-not-supported"));
        }

        return new PlatformCapabilitySnapshot(platform, capabilities);
    }
}

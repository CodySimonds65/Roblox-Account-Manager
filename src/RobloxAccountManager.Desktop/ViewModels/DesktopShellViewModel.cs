using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Desktop.ViewModels;

public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(string key, string title, string description)
    {
        Key = key;
        Title = title;
        Description = description;
    }

    public string Key { get; }
    public string Title { get; }
    public string Description { get; }
    public override string ToString() => Title;
}

public sealed class DesktopShellViewModel : INotifyPropertyChanged
{
    private NavigationItemViewModel _selectedPage;

    public DesktopShellViewModel(
        IPlatformCapabilities capabilities,
        IPlatformUpdateInstaller? updateInstaller = null)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        UpdateInstaller = updateInstaller;
        Pages = new ObservableCollection<NavigationItemViewModel>(
        [
            new("accounts", "Accounts", "Manage isolated Roblox accounts and profiles."),
            new("browser", "Browser", "Open a persistent, isolated account browser."),
            new("presets", "Presets", "Edit and apply per-account game settings."),
            new("queue", "Launch Queue", "Serialized launches with fresh URI retries."),
            new("activity", "Activity", "Review sanitized launch and client events."),
            new("settings", "Settings", "Configure platform, consent, and update behavior."),
            new("diagnostics", "Diagnostics", "Inspect capability and process diagnostics."),
            new("plugins", "Plugins", "Manage signed plugins and requested capabilities."),
            new("clients", "Clients", "Focus, tile, and close external Roblox clients.")
        ]);
        _selectedPage = Pages[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<NavigationItemViewModel> Pages { get; }
    public IPlatformCapabilities Capabilities { get; }
    public IPlatformUpdateInstaller? UpdateInstaller { get; }

    public NavigationItemViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (ReferenceEquals(_selectedPage, value))
                return;
            _selectedPage = value;
            OnPropertyChanged();
        }
    }

    public string PlatformLabel => Capabilities.Platform switch
    {
        RobloxPlatform.MacOS => "macOS",
        RobloxPlatform.Windows => "Windows",
        _ => "Unsupported platform"
    };

    public string PageStatus => SelectedPage.Key == "clients" &&
        Capabilities.Get(CapabilityNames.ExternalRobloxWindow).Status == CapabilityStatus.RequiresPermission
        ? Capabilities.Get(CapabilityNames.ExternalRobloxWindow).Description
        : SelectedPage.Description;

    public IReadOnlyList<CapabilityDescriptor> CurrentPageCapabilities => SelectedPage.Key switch
    {
        "accounts" => [Capabilities.Get(CapabilityNames.BrowserProfileDeletion)],
        "clients" => [Capabilities.Get(CapabilityNames.ExternalRobloxWindow)],
        "plugins" => [Capabilities.Get(CapabilityNames.PluginHost), Capabilities.Get(CapabilityNames.InputAutomation)],
        _ => Array.Empty<CapabilityDescriptor>()
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

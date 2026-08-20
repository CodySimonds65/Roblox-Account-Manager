using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Data;
using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Desktop.ViewModels;

public sealed class NavigationItemViewModel(string key, string title, string description)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public override string ToString() => Title;
}

public sealed class DesktopShellViewModel : INotifyPropertyChanged
{
    private NavigationItemViewModel _selectedPage;
    private AccountProfile? _selectedAccount;
    private GamePreset? _selectedPreset;
    private string _activity = string.Empty;

    public DesktopShellViewModel(
        IPlatformCapabilities capabilities,
        AccountStore accountStore,
        GamePresetStore presetStore,
        SettingsStore settingsStore,
        IPlatformUpdateInstaller? updateInstaller = null,
        IPlatformUpdateSource? updateSource = null,
        IRobloxSettingsAdapter? robloxSettings = null,
        IPluginHostFacade? pluginHost = null)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        AccountsStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        PresetsStore = presetStore ?? throw new ArgumentNullException(nameof(presetStore));
        SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        UpdateInstaller = updateInstaller;
        UpdateSource = updateSource;
        RobloxSettings = robloxSettings;
        PluginHost = pluginHost;
        Pages = new ObservableCollection<NavigationItemViewModel>(
        [
            new("accounts", "Accounts", "Manage isolated Roblox account profiles."),
            new("browser", "Browser", "Sign in with a persistent, isolated account session."),
            new("presets", "Presets", "Edit, import, export, and apply game presets."),
            new("queue", "Launch Queue", "Launch selected accounts with verified process handoff."),
            new("clients", "Clients", "Focus, tile, and close verified external Roblox clients."),
            new("activity", "Activity", "Review sanitized launch and client events."),
            new("settings", "Settings", "Configure queue, Roblox, storage, and update behavior."),
            new("plugins", "Plugins", "Manage local plugins and platform capabilities."),
            new("diagnostics", "Diagnostics", "Inspect platform trust and permission status.")
        ]);
        _selectedPage = Pages[0];
        Accounts = new ObservableCollection<AccountProfile>();
        Presets = new ObservableCollection<GamePreset>();
        Queue = new ObservableCollection<LaunchQueueItem>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<NavigationItemViewModel> Pages { get; }
    public ObservableCollection<AccountProfile> Accounts { get; }
    public ObservableCollection<GamePreset> Presets { get; }
    public ObservableCollection<LaunchQueueItem> Queue { get; }
    public IPlatformCapabilities Capabilities { get; }
    public AccountStore AccountsStore { get; }
    public GamePresetStore PresetsStore { get; }
    public SettingsStore SettingsStore { get; }
    public IPlatformUpdateInstaller? UpdateInstaller { get; }
    public IPlatformUpdateSource? UpdateSource { get; }
    public IRobloxSettingsAdapter? RobloxSettings { get; }
    public IPluginHostFacade? PluginHost { get; }
    public LauncherSettings Settings { get; private set; } = new();

    public NavigationItemViewModel SelectedPage
    {
        get => _selectedPage;
        set { if (!ReferenceEquals(_selectedPage, value)) { _selectedPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageStatus)); } }
    }

    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set { if (!ReferenceEquals(_selectedAccount, value)) { _selectedAccount = value; OnPropertyChanged(); } }
    }

    public GamePreset? SelectedPreset
    {
        get => _selectedPreset;
        set { if (!ReferenceEquals(_selectedPreset, value)) { _selectedPreset = value; OnPropertyChanged(); } }
    }

    public string Activity
    {
        get => _activity;
        private set { _activity = value; OnPropertyChanged(); }
    }

    public string PlatformLabel => Capabilities.Platform == RobloxPlatform.MacOS ? "macOS" : Capabilities.Platform.ToString();

    public string PageStatus => SelectedPage.Key == "clients" &&
        Capabilities.Get(CapabilityNames.ExternalRobloxWindow).Status == CapabilityStatus.RequiresPermission
        ? Capabilities.Get(CapabilityNames.ExternalRobloxWindow).Description
        : SelectedPage.Description;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Accounts.Clear();
        foreach (var account in await AccountsStore.LoadAsync(cancellationToken).ConfigureAwait(false)) Accounts.Add(account);
        Presets.Clear();
        foreach (var preset in GamePresetStore.EnsureBuiltIns(await PresetsStore.LoadAsync(cancellationToken).ConfigureAwait(false))) Presets.Add(preset);
        SelectedPreset = Presets.FirstOrDefault(preset => preset.Name.Equals("Dungeon Quest Reborn", StringComparison.OrdinalIgnoreCase));
        Settings = await SettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Settings.GameSettings ??= new GameSettings();
        Settings.GameOverrides ??= new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase);
        AppendActivity($"Ready. Loaded {Accounts.Count} account profile(s) and {Presets.Count} preset(s).");
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await AccountsStore.SaveAsync(Accounts, cancellationToken).ConfigureAwait(false);
        await PresetsStore.SaveAsync(Presets.Where(x => !x.IsBuiltIn), cancellationToken).ConfigureAwait(false);
        await SettingsStore.SaveAsync(Settings, cancellationToken).ConfigureAwait(false);
    }

    public void ImportSettings(LauncherSettings settings)
    {
        Settings = settings ?? new LauncherSettings();
        Settings.GameSettings ??= new GameSettings();
        Settings.GameOverrides ??= new Dictionary<string, GameSettings>(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(Settings));
    }


    public void AppendActivity(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Activity = string.IsNullOrEmpty(Activity) ? line : Activity + Environment.NewLine + line;
    }

    public static string Describe(CapabilityDescriptor capability) =>
        $"{capability.Name}: {capability.Status} — {capability.Description}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

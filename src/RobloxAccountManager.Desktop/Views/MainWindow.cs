using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using System.Text.Json;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Data;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Core.Models;
using RobloxAccountManager.Desktop.Services;
using RobloxAccountManager.Desktop.ViewModels;
using RobloxAccountManager.Platform.MacOS;
using CoreClientWindowManager = RobloxAccountManager.Core.Contracts.IClientWindowManager;
using CoreRobloxLaunchRequest = RobloxAccountManager.Core.Contracts.RobloxLaunchRequest;
using CoreRobloxProcessInfo = RobloxAccountManager.Core.Contracts.RobloxProcessInfo;

namespace RobloxAccountManager.Desktop.Views;

public sealed class MainWindow : Window
{
    private readonly DesktopShellViewModel _viewModel;
    private readonly ContentControl _content = new();
    private readonly TextBlock _pageTitle = new();
    private readonly TextBlock _pageDescription = new();
    private readonly TextBox _activity = new();
    private readonly ContentControl _browserHost = new();
    private readonly AvaloniaAccountBrowserSessionService _browserSessions;
    private readonly SerializedLaunchCoordinator? _launches;
    private readonly CoreClientWindowManager? _clients;
    private CancellationTokenSource? _launchCancellation;
    private string? _lastGameUrl;
    private readonly HashSet<string> _queueSelectedAccounts = new(StringComparer.Ordinal);

    public MainWindow(
        DesktopShellViewModel viewModel,
        AvaloniaAccountBrowserSessionService browserSessions,
        SerializedLaunchCoordinator? launches = null,
        CoreClientWindowManager? clients = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _browserSessions = browserSessions ?? throw new ArgumentNullException(nameof(browserSessions));
        _launches = launches;
        _clients = clients;
        Title = "Roblox Account Manager";
        Width = 1240;
        Height = 820;
        MinWidth = 980;
        MinHeight = 620;

        var navigation = new ListBox
        {
            Width = 205,
            Margin = new Thickness(12),
            ItemsSource = _viewModel.Pages,
            SelectedItem = _viewModel.SelectedPage,
            Background = Brushes.Transparent
        };
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is NavigationItemViewModel page)
            {
                _viewModel.SelectedPage = page;
                RenderPage();
            }
        };

        _pageTitle.FontSize = 28;
        _pageTitle.FontWeight = FontWeight.Bold;
        _pageDescription.TextWrapping = TextWrapping.Wrap;
        _pageDescription.Opacity = 0.8;
        _activity.IsReadOnly = true;
        _activity.AcceptsReturn = true;
        _activity.TextWrapping = TextWrapping.NoWrap;

        var header = new StackPanel { Spacing = 5 };
        header.Children.Add(_pageTitle);
        header.Children.Add(_pageDescription);
        header.Children.Add(new TextBlock { Text = _viewModel.PlatformLabel, FontSize = 12, Opacity = 0.6 });

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(28) };
        page.Children.Add(header);
        Grid.SetRow(_content, 1);
        page.Children.Add(_content);
        Grid.SetRow(_activity, 2);
        _activity.Height = 115;
        _activity.Margin = new Thickness(0, 16, 0, 0);
        page.Children.Add(_activity);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(navigation);
        Grid.SetColumn(page, 1);
        grid.Children.Add(new ScrollViewer { Content = page });
        Content = grid;
        Opened += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _viewModel.LoadAsync();
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DesktopShellViewModel.Activity)) _activity.Text = _viewModel.Activity;
            };
            _activity.Text = _viewModel.Activity;
            RenderPage();
            if (_viewModel.Accounts.Count > 0)
            {
                _viewModel.SelectedAccount = _viewModel.Accounts[0];
                await OpenAccountAsync(_viewModel.SelectedAccount);
            }
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Startup error: {exception.Message}");
            RenderPage();
        }
    }

    private void RenderPage()
    {
        _pageTitle.Text = _viewModel.SelectedPage.Title;
        _pageDescription.Text = _viewModel.PageStatus;
        _content.Content = _viewModel.SelectedPage.Key switch
        {
            "accounts" => BuildAccountsPage(),
            "browser" => BuildBrowserPage(),
            "presets" => BuildPresetsPage(),
            "queue" => BuildQueuePage(),
            "clients" => BuildClientsPage(),
            "activity" => BuildActivityPage(),
            "settings" => BuildSettingsPage(),
            "plugins" => BuildPluginsPage(),
            "updates" => BuildUpdatesPage(),
            "diagnostics" => BuildDiagnosticsPage(),
            _ => new TextBlock { Text = "Select a page." }
        };
    }

    private Control BuildAccountsPage()
    {
        var list = new ListBox { Height = 320 };
        foreach (var account in _viewModel.Accounts)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(4) };
            var queue = new CheckBox { Content = "Queue", IsChecked = _queueSelectedAccounts.Contains(account.Id) };
            queue.Click += (_, _) => { if (queue.IsChecked == true) _queueSelectedAccounts.Add(account.Id); else _queueSelectedAccounts.Remove(account.Id); };
            row.Children.Add(queue);
            var favorite = new CheckBox { Content = "Favorite", IsChecked = account.IsFavorite, Margin = new Thickness(10, 0, 0, 0) };
            favorite.Click += async (_, _) => { account.IsFavorite = favorite.IsChecked == true; await SaveAsync(); };
            Grid.SetColumn(favorite, 1);
            row.Children.Add(favorite);
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(new TextBlock { Text = account.Label, FontWeight = FontWeight.SemiBold });
            details.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(account.Group) ? "No group" : account.Group, FontSize = 12, Opacity = 0.65 });
            Grid.SetColumn(details, 2);
            row.Children.Add(details);
            var open = new Button { Content = "Open", Tag = account, Margin = new Thickness(8, 0, 0, 0) };
            open.Click += async (_, _) => { _viewModel.SelectedAccount = account; await OpenAccountAsync(account); RenderPage(); };
            Grid.SetColumn(open, 3);
            row.Children.Add(open);
            list.Items.Add(new Border { Child = row, Padding = new Thickness(8), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 0, 1) });
        }

        var add = new Button { Content = "Add account profile" };
        add.Click += async (_, _) =>
        {
            var values = await PromptAsync("New account profile", "Roblox account", string.Empty);
            if (values is null) return;
            _viewModel.Accounts.Add(new AccountProfile { Label = values.Value.Label, Group = values.Value.Group, SortOrder = _viewModel.Accounts.Count });
            await SaveAsync();
            RenderPage();
        };
        var edit = new Button { Content = "Edit selected", IsEnabled = _viewModel.SelectedAccount is not null };
        edit.Click += async (_, _) =>
        {
            if (_viewModel.SelectedAccount is null) return;
            var account = _viewModel.SelectedAccount;
            var values = await PromptAsync("Edit account profile", account.Label, account.Group);
            if (values is null) return;
            account.Label = values.Value.Label; account.Group = values.Value.Group;
            await SaveAsync(); RenderPage();
        };
        var remove = new Button { Content = "Remove selected", IsEnabled = _viewModel.SelectedAccount is not null };
        remove.Click += async (_, _) =>
        {
            if (_viewModel.SelectedAccount is null || !await ConfirmAsync($"Remove '{_viewModel.SelectedAccount.Label}' and clear its browser session?")) return;
            var account = _viewModel.SelectedAccount;
            try { await _browserSessions.RemoveAsync(account.Id); } catch (Exception exception) { _viewModel.AppendActivity($"Browser data cleanup skipped: {exception.Message}"); }
            _viewModel.Accounts.Remove(account); _viewModel.SelectedAccount = null; await SaveAsync(); RenderPage();
        };
        var export = new Button { Content = "Export profiles" };
        export.Click += async (_, _) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-account-manager-profile-export.json");
            await ProfileTransferService.ExportAsync(path, _viewModel.Accounts, _viewModel.Presets, _viewModel.Settings);
            _viewModel.AppendActivity($"Exported profiles, presets, and settings to {path}. Browser cookies were not included.");
        };
        var import = new Button { Content = "Import profiles" };
        import.Click += async (_, _) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-account-manager-profile-export.json");
            if (!File.Exists(path)) { _viewModel.AppendActivity($"Import file not found: {path}"); return; }
            try
            {
                var package = await ProfileTransferService.ImportAsync(path);
                foreach (var account in package.Accounts)
                {
                    account.Id = Guid.NewGuid().ToString("N");
                    account.SortOrder = _viewModel.Accounts.Count;
                    _viewModel.Accounts.Add(account);
                }
                foreach (var preset in package.Presets.Where(x => _viewModel.Presets.All(existing => !string.Equals(existing.Name, x.Name, StringComparison.OrdinalIgnoreCase)))) _viewModel.Presets.Add(preset);
                _viewModel.ImportSettings(package.Settings);
                await SaveAsync();
                _viewModel.AppendActivity($"Imported {package.Accounts.Count} profile(s) and {package.Presets.Count} preset(s). Sign in again in each new browser session.");
                RenderPage();
            }
            catch (Exception exception) { _viewModel.AppendActivity($"Profile import rejected: {exception.Message}"); }
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(add); buttons.Children.Add(edit); buttons.Children.Add(remove); buttons.Children.Add(export); buttons.Children.Add(import);
        return Card(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Account profiles", FontSize = 18, FontWeight = FontWeight.SemiBold }, list, buttons, new TextBlock { Text = "Favorites are shown first. Browser data stays local and is never exported.", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap } } });
    }

    private Control BuildBrowserPage()
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var login = new Button { Content = "Login" };
        login.Click += async (_, _) => { if (_viewModel.SelectedAccount is not null) await _browserSessions.NavigateAsync(_viewModel.SelectedAccount.Id, new Uri("https://www.roblox.com/login")); };
        var home = new Button { Content = "Roblox home" };
        home.Click += async (_, _) => { if (_viewModel.SelectedAccount is not null) await _browserSessions.NavigateAsync(_viewModel.SelectedAccount.Id, new Uri("https://www.roblox.com/home")); };
        toolbar.Children.Add(login); toolbar.Children.Add(home);
        if (_viewModel.SelectedAccount is null)
            return Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Select an account profile first." }, toolbar } });
        _ = OpenAccountAsync(_viewModel.SelectedAccount);
        _browserHost.MinHeight = 480;
        _browserHost.Content ??= new TextBlock { Text = "Opening isolated Roblox session..." };
        return new StackPanel { Spacing = 10, Children = { toolbar, _browserHost } };
    }

    private Control BuildPresetsPage()
    {
        var list = new ListBox { Height = 280, ItemsSource = _viewModel.Presets, SelectedItem = _viewModel.SelectedPreset };
        list.SelectionChanged += (_, _) => { _viewModel.SelectedPreset = list.SelectedItem as GamePreset; RenderPage(); };
        var name = new TextBox { PlaceholderText = "Preset name", Text = _viewModel.SelectedPreset?.Name ?? string.Empty };
        var url = new TextBox { PlaceholderText = "https://www.roblox.com/games/123/example", Text = _viewModel.SelectedPreset?.Url ?? string.Empty };
        var save = new Button { Content = "Save preset" };
        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || !GamePreset.TryNormalizeRobloxGameUrl(url.Text ?? string.Empty, out var normalized))
            { _viewModel.AppendActivity("Preset rejected: enter a name and a valid Roblox game URL."); return; }
            if (_viewModel.SelectedPreset is null || _viewModel.SelectedPreset.IsBuiltIn)
                _viewModel.Presets.Add(new GamePreset(name.Text.Trim(), normalized));
            else
            {
                var index = _viewModel.Presets.IndexOf(_viewModel.SelectedPreset);
                if (index >= 0) _viewModel.Presets[index] = _viewModel.SelectedPreset with { Name = name.Text.Trim(), Url = normalized };
            }
            await SaveAsync(); RenderPage();
        };
        var remove = new Button { Content = "Remove preset", IsEnabled = _viewModel.SelectedPreset is { IsBuiltIn: false } };
        remove.Click += async (_, _) => { if (_viewModel.SelectedPreset is { IsBuiltIn: false } preset) { _viewModel.Presets.Remove(preset); await SaveAsync(); RenderPage(); } };
        var export = new Button { Content = "Export JSON" };
        export.Click += async (_, _) => { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-presets.json"); await PresetTransferService.ExportAsync(path, _viewModel.Presets); _viewModel.AppendActivity($"Exported presets to {path}."); };
        var import = new Button { Content = "Import JSON" };
        import.Click += async (_, _) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-presets.json");
            if (!File.Exists(path)) { _viewModel.AppendActivity($"Preset import file not found: {path}"); return; }
            try
            {
                foreach (var preset in await PresetTransferService.ImportAsync(path))
                    if (_viewModel.Presets.All(existing => !string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase))) _viewModel.Presets.Add(preset);
                await SaveAsync(); RenderPage();
            }
            catch (Exception exception) { _viewModel.AppendActivity($"Preset import rejected: {exception.Message}"); }
        };
        var controls = new StackPanel { Spacing = 8, Children = { name, url, new WrapPanel { Children = { save, remove, export, import } } } };
        return Card(new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 18, Children = { list, controls } });
    }

    private Control BuildQueuePage()
    {
        var selected = _viewModel.Accounts.Where(x => _queueSelectedAccounts.Contains(x.Id)).ToList();
        var picker = new ComboBox { ItemsSource = _viewModel.Presets, SelectedItem = _viewModel.SelectedPreset ?? _viewModel.Presets.FirstOrDefault() };
        picker.SelectionChanged += (_, _) => _viewModel.SelectedPreset = picker.SelectedItem as GamePreset;
        var consent = new CheckBox { Content = "I consent to macOS multi-instance semaphore changes", IsChecked = _viewModel.Settings.MultiInstanceConsentGranted };
        var launch = new Button { Content = _launches is null ? "Launch unavailable until Roblox trust is configured" : "Launch selected accounts", IsEnabled = _launches is not null };
        launch.Click += async (_, _) => await RunLaunchQueueAsync(consent.IsChecked == true, picker.SelectedItem as GamePreset, selected);
        var cancel = new Button { Content = "Cancel", IsEnabled = _launchCancellation is not null };
        cancel.Click += (_, _) => _launchCancellation?.Cancel();
        var queueList = new ListBox { ItemsSource = _viewModel.Queue, Height = 230 };
        var info = new TextBlock { Text = selected.Count == 0 ? "Favorite an account or open one from Accounts to include it in the queue." : $"{selected.Count} account(s) selected." , TextWrapping = TextWrapping.Wrap };
        return Card(new StackPanel { Spacing = 10, Children = { info, new TextBlock { Text = "Game preset", FontWeight = FontWeight.SemiBold }, picker, consent, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { launch, cancel } }, queueList } });
    }

    private Control BuildClientsPage()
    {
        var panel = new StackPanel { Spacing = 8 };
        if (_clients is null)
        {
            panel.Children.Add(new TextBlock { Text = "Client management is unavailable on this platform." });
            return Card(panel);
        }
        panel.Children.Add(new TextBlock { Text = _viewModel.Capabilities.Get(CapabilityNames.ExternalRobloxWindow).Description, TextWrapping = TextWrapping.Wrap });
        var refresh = new Button { Content = "Refresh clients" };
        refresh.Click += async (_, _) => await RefreshClientsAsync(panel);
        var tile = new Button { Content = "Tile all clients" };
        tile.Click += async (_, _) =>
        {
            var windows = await _clients.GetWindowsAsync();
            var success = await _clients.TileAsync(windows);
            _viewModel.AppendActivity(success ? "Tiled verified Roblox clients." : "Could not tile clients; check Accessibility and Apple Events permissions.");
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refresh, tile } });
        _ = RefreshClientsAsync(panel);
        return Card(panel);
    }

    private async Task RefreshClientsAsync(StackPanel panel)
    {
        if (_clients is null) return;
        while (panel.Children.Count > 2) panel.Children.RemoveAt(2);
        var windows = await _clients.GetWindowsAsync();
        foreach (var window in windows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = $"PID {window.Process.Pid} · {Path.GetFileName(window.Process.ExecutablePath)}", Width = 320, VerticalAlignment = VerticalAlignment.Center });
            var focus = new Button { Content = "Focus" };
            focus.Click += async (_, _) => _viewModel.AppendActivity(await _clients.FocusAsync(window) ? "Focused client." : "Focus denied; check Accessibility permission.");
            var close = new Button { Content = "Close" };
            close.Click += async (_, _) => { try { await _clients.CloseAsync(new CoreRobloxProcessInfo(window.Process, true)); _viewModel.AppendActivity("Requested a verified client close."); } catch (Exception exception) { _viewModel.AppendActivity($"Client close rejected: {exception.Message}"); } };
            row.Children.Add(focus); row.Children.Add(close); panel.Children.Add(row);
        }
        if (windows.Count == 0) panel.Children.Add(new TextBlock { Text = "No RAM-managed Roblox clients are running.", Opacity = 0.65 });
    }

    private Control BuildActivityPage() => Card(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Sanitized activity", FontWeight = FontWeight.SemiBold }, new TextBlock { Text = _viewModel.Activity, TextWrapping = TextWrapping.Wrap } } });

    private Control BuildSettingsPage()
    {
        var timeout = new NumericUpDown { Minimum = 15, Maximum = 180, Value = _viewModel.Settings.LaunchTimeoutSeconds, Increment = 5 };
        var delay = new NumericUpDown { Minimum = 0, Maximum = 60, Value = _viewModel.Settings.LaunchDelaySeconds, Increment = 1 };
        var continueOnFailure = new CheckBox { Content = "Continue after a failed account", IsChecked = _viewModel.Settings.ContinueOnFailure };
        var remember = new CheckBox { Content = "Remember account and preset selections", IsChecked = _viewModel.Settings.RememberSelections };
        var updates = new CheckBox { Content = "Check for updates", IsChecked = _viewModel.Settings.UpdateChecksEnabled };
        var settingsConsent = new CheckBox { Content = "I consent to managed Roblox settings changes", IsChecked = _viewModel.Settings.RobloxSettingsConsentGranted };
        var save = new Button { Content = "Save settings" };
        save.Click += async (_, _) =>
        {
            _viewModel.Settings.LaunchTimeoutSeconds = (int)(timeout.Value ?? 45);
            _viewModel.Settings.LaunchDelaySeconds = (int)(delay.Value ?? 0);
            _viewModel.Settings.ContinueOnFailure = continueOnFailure.IsChecked == true;
            _viewModel.Settings.RememberSelections = remember.IsChecked == true;
            _viewModel.Settings.UpdateChecksEnabled = updates.IsChecked == true;
            _viewModel.Settings.RobloxSettingsConsentGranted = settingsConsent.IsChecked == true;
            if (_viewModel.RobloxSettings is not null && _viewModel.Settings.RobloxSettingsConsentGranted)
            {
                var result = await _viewModel.RobloxSettings.ApplyAsync(_viewModel.Settings.GameSettings);
                _viewModel.AppendActivity($"Roblox settings: {result.Applied.Count} applied, {result.Skipped.Count} skipped.");
            }
            await SaveAsync();
        };
        var settings = new StackPanel { Spacing = 10 };
        settings.Children.Add(new TextBlock { Text = "Queue and storage", FontSize = 18, FontWeight = FontWeight.SemiBold });
        settings.Children.Add(new TextBlock { Text = "Launch timeout (seconds)" }); settings.Children.Add(timeout);
        settings.Children.Add(new TextBlock { Text = "Delay between accounts (seconds)" }); settings.Children.Add(delay);
        settings.Children.Add(continueOnFailure); settings.Children.Add(remember); settings.Children.Add(updates); settings.Children.Add(settingsConsent);
        settings.Children.Add(new Separator());
        settings.Children.Add(new TextBlock { Text = "Roblox settings", FontSize = 18, FontWeight = FontWeight.SemiBold });
        settings.Children.Add(new TextBlock { Text = _viewModel.RobloxSettings is null ? "No macOS Roblox settings adapter is configured." : string.Join(Environment.NewLine, _viewModel.RobloxSettings.Capabilities.Select(x => $"{x.Name}: {x.Status} — {x.Description}")), TextWrapping = TextWrapping.Wrap });
        settings.Children.Add(save);
        return Card(settings);
    }

    private Control BuildPluginsPage()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Plugins", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "macOS plugins use the Unix transport. Windows-only input and screen capabilities remain unavailable.", TextWrapping = TextWrapping.Wrap });
        foreach (var capability in _viewModel.PluginHost?.Capabilities ?? Array.Empty<PluginCapabilityResult>())
            panel.Children.Add(new TextBlock { Text = $"{capability.Capability}: {capability.Status} {capability.StableFailureCode ?? string.Empty}", Opacity = 0.8 });
        var install = new Button { Content = "Install from ~/Desktop/roblox-plugin" };
        install.Click += async (_, _) =>
        {
            if (_viewModel.PluginHost is null) return;
            var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-plugin");
            if (!await ConfirmAsync("Install this local plugin and review its declared capabilities before starting it?")) return;
            var result = await _viewModel.PluginHost.InstallFromDirectoryAsync(source, userConfirmed: true);
            _viewModel.AppendActivity(result.Succeeded ? $"Installed plugin {result.PluginId}." : $"Plugin install rejected: {result.DiagnosticCode}.");
            RenderPage();
        };
        panel.Children.Add(install);
        var refresh = new Button { Content = "Refresh installed plugins" };
        refresh.Click += async (_, _) =>
        {
            if (_viewModel.PluginHost is null) return;
            var ids = await _viewModel.PluginHost.GetInstalledPluginIdsAsync();
            var running = await _viewModel.PluginHost.GetRunningPluginIdsAsync();
            _viewModel.AppendActivity(ids.Count == 0 ? "No macOS plugins are installed." : $"Installed plugins: {string.Join(", ", ids)}; running: {string.Join(", ", running)}");
            RenderPage();
        };
        panel.Children.Add(refresh);
        if (_viewModel.PluginHost is not null)
        {
            var ids = _viewModel.PluginHost.GetInstalledPluginIdsAsync().AsTask().GetAwaiter().GetResult();
            var running = _viewModel.PluginHost.GetRunningPluginIdsAsync().AsTask().GetAwaiter().GetResult();
            foreach (var id in ids)
            {
                var start = new Button { Content = running.Contains(id) ? "Running" : "Start", IsEnabled = !running.Contains(id) };
                start.Click += async (_, _) =>
                {
                    var requested = await _viewModel.PluginHost.GetRequestedCapabilitiesAsync(id);
                    if (!await ConfirmAsync($"Grant plugin {id} these declared capabilities: {string.Join(", ", requested)}?")) return;
                    var result = await _viewModel.PluginHost.StartAsync(id, userConfirmed: true);
                    _viewModel.AppendActivity(result.Succeeded ? $"Started plugin {id}." : $"Plugin start rejected: {result.DiagnosticCode}.");
                    RenderPage();
                };
                var stop = new Button { Content = "Stop", IsEnabled = running.Contains(id) };
                stop.Click += async (_, _) =>
                {
                    var result = await _viewModel.PluginHost.StopAsync(id);
                    _viewModel.AppendActivity(result.Succeeded ? $"Stopped plugin {id}." : $"Plugin stop rejected: {result.DiagnosticCode}.");
                    RenderPage();
                };
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = id, Width = 300, VerticalAlignment = VerticalAlignment.Center }, start, stop } });
            }
        }
        return Card(panel);
    }

    private Control BuildDiagnosticsPage()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Platform diagnostics", FontSize = 18, FontWeight = FontWeight.SemiBold });
        foreach (var capability in _viewModel.Capabilities.Snapshot.Capabilities)
            panel.Children.Add(new Border { Padding = new Thickness(8), BorderBrush = capability.Status == CapabilityStatus.Supported ? Brushes.ForestGreen : Brushes.DarkOrange, BorderThickness = new Thickness(1), Child = new TextBlock { Text = DesktopShellViewModel.Describe(capability), TextWrapping = TextWrapping.Wrap } });
        panel.Children.Add(new TextBlock { Text = "Unsigned macOS packages require explicit approval in System Settings → Privacy & Security → Open Anyway. Roblox launch remains fail-closed unless the official Roblox bundle passes its configured Team ID verification.", TextWrapping = TextWrapping.Wrap });
        return Card(panel);
    }

    private Control BuildUpdatesPage()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "macOS updates", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Place an update manifest at ~/Desktop/roblox-account-manager-update.json. The manifest must point to a local PKG and include its SHA-256, architecture, package identity, and version metadata. The package is revalidated before Apple Installer opens.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Unsigned development packages are intentionally supported, but always require a fresh confirmation and macOS Privacy & Security approval. Unsigned consent is never imported from a profile export.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DarkOrange
        });
        var install = new Button { Content = _viewModel.UpdateInstaller is null ? "Updates unavailable" : "Validate and install Desktop manifest", IsEnabled = _viewModel.UpdateInstaller is not null };
        install.Click += async (_, _) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-account-manager-update.json");
            try
            {
                if (_viewModel.UpdateInstaller is null) throw new InvalidOperationException("update-installer-unavailable");
                var package = JsonSerializer.Deserialize<UpdatePackage>(await File.ReadAllTextAsync(path))
                    ?? throw new InvalidDataException("The update manifest is empty.");
                var warning = package.IsUnsigned
                    ? "This is an explicitly unsigned development package. macOS will require Open Anyway approval. Continue only if you trust the source and checksum."
                    : "This package will be checksum- and identity-validated before Apple Installer opens. Continue?";
                if (!await ConfirmAsync(warning)) return;
                if (package.IsUnsigned) _viewModel.Settings.UnsignedUpdatesConsentGranted = true;
                var result = await _viewModel.UpdateInstaller.InstallAsync(package, userConfirmed: true);
                _viewModel.AppendActivity(result.Accepted ? "Update package verified; Apple Installer opened." : $"Update rejected: {result.DiagnosticCode}.");
                await SaveAsync();
            }
            catch (Exception exception)
            {
                _viewModel.AppendActivity($"Update manifest rejected: {exception.Message}");
            }
        };
        panel.Children.Add(install);
        return Card(panel);
    }

    private async Task RunLaunchQueueAsync(bool consent, GamePreset? preset, IReadOnlyList<AccountProfile> accounts)
    {
        if (!consent) { _viewModel.AppendActivity("Launch blocked: explicit macOS multi-instance consent is required."); return; }
        if (_launches is null || preset is null || !GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out var gameUrl)) { _viewModel.AppendActivity("Launch blocked: configure trusted Roblox and a valid game preset."); return; }
        if (accounts.Count == 0) { _viewModel.AppendActivity("Launch blocked: favorite or open at least one account."); return; }
        _viewModel.Settings.MultiInstanceConsentGranted = true;
        _lastGameUrl = gameUrl;
        _viewModel.Queue.Clear();
        foreach (var account in accounts) _viewModel.Queue.Add(new LaunchQueueItem(account));
        _launchCancellation?.Dispose(); _launchCancellation = new CancellationTokenSource();
        try
        {
            foreach (var item in _viewModel.Queue)
            {
                _launchCancellation.Token.ThrowIfCancellationRequested();
                item.State = LaunchQueueState.Launching; item.Detail = "Waiting for Roblox launch URI";
                GameSettings? gameSettings = preset.Settings;
                if (_viewModel.Settings.GameOverrides.TryGetValue(gameUrl, out var urlSettings))
                {
                    gameSettings = GameSettings.Resolve(new GameSettings(), gameSettings, urlSettings);
                }
                var scopedSettings = GameSettings.Resolve(
                    _viewModel.Settings.GameSettings,
                    gameSettings,
                    item.Account.GameSettings);
                var request = new CoreRobloxLaunchRequest(item.Account.Id, async cancellationToken =>
                {
                    if (_viewModel.RobloxSettings is not null && _viewModel.Settings.RobloxSettingsConsentGranted && scopedSettings.HasOverrides)
                    {
                        var settingsResult = await _viewModel.RobloxSettings.ApplyAsync(scopedSettings, cancellationToken);
                        _viewModel.AppendActivity($"{item.Label}: Roblox settings applied={settingsResult.Applied.Count}, skipped={settingsResult.Skipped.Count}.");
                        if (!settingsResult.Succeeded)
                        {
                            await _viewModel.RobloxSettings.RecoverAsync(cancellationToken);
                            throw new InvalidOperationException(settingsResult.DiagnosticCode ?? "roblox-settings-apply-failed");
                        }
                    }
                    await OpenAccountAsync(item.Account);
                    var pending = _browserSessions.BeginLaunchCapture(item.Account.Id, cancellationToken);
                    await _browserSessions.NavigateAsync(item.Account.Id, new Uri(gameUrl), cancellationToken);
                    var navigation = await pending.ConfigureAwait(true);
                    if (!navigation.TryConsumeLaunchUri(out var uri) || uri is null) throw new InvalidOperationException("No Roblox launch URI was captured.");
                    return uri;
                }, MaxAttempts: 3, RobloxBundlePath: await DiscoverRobloxBundleAsync(), UserConsentedToMultiInstanceChanges: true,
                    VerificationTimeout: TimeSpan.FromSeconds(Math.Clamp(_viewModel.Settings.LaunchTimeoutSeconds, 15, 180)));
                LaunchResult result;
                try
                {
                    result = await _launches.LaunchAsync(request, _launchCancellation.Token);
                }
                catch (OperationCanceledException) when (_launchCancellation is not null && !_launchCancellation.IsCancellationRequested)
                {
                    item.State = LaunchQueueState.Failed;
                    item.Detail = "Launch canceled by settings or browser failure";
                    _viewModel.AppendActivity($"{item.Label}: {item.Detail}.");
                    if (!_viewModel.Settings.ContinueOnFailure) break;
                    continue;
                }
                catch (Exception exception)
                {
                    item.State = LaunchQueueState.Failed;
                    item.Detail = LaunchDiagnostics.SanitiseCode(exception.Message);
                    _viewModel.AppendActivity($"{item.Label}: launch failed safely ({item.Detail}).");
                    if (!_viewModel.Settings.ContinueOnFailure) break;
                    continue;
                }
                item.State = result.Succeeded ? LaunchQueueState.Running : LaunchQueueState.Failed;
                item.Detail = result.Succeeded ? "Verified process started" : result.FailureKind.ToString();
                _viewModel.AppendActivity($"{item.Label}: {item.Detail}.");
                if (!result.Succeeded && !_viewModel.Settings.ContinueOnFailure) break;
                if (_viewModel.Settings.LaunchDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_viewModel.Settings.LaunchDelaySeconds), _launchCancellation.Token);
            }
        }
        catch (OperationCanceledException) { foreach (var item in _viewModel.Queue.Where(x => x.State == LaunchQueueState.Waiting || x.State == LaunchQueueState.Launching)) { item.State = LaunchQueueState.Canceled; item.Detail = "Canceled"; } _viewModel.AppendActivity("Launch queue canceled."); }
        catch (Exception exception) { _viewModel.AppendActivity($"Launch queue failed safely: {exception.Message}"); }
        finally { _launchCancellation?.Dispose(); _launchCancellation = null; await SaveAsync(); RenderPage(); }
    }

    private async Task<string?> DiscoverRobloxBundleAsync()
    {
        var team = TrustedRobloxIdentityConfiguration.LoadTeamIdentifier();
        if (string.IsNullOrWhiteSpace(team)) return null;
        var discovery = new MacBundleDiscovery(requiredTeamIdentifier: team);
        var bundle = await discovery.DiscoverAsync();
        return bundle?.BundlePath;
    }

    private async Task OpenAccountAsync(AccountProfile account)
    {
        await _browserSessions.CreateAsync(account.Id, account.Label);
        _browserHost.Content = _browserSessions.GetView(account.Id);
        await _browserSessions.NavigateAsync(account.Id, new Uri("https://www.roblox.com/home"));
        _viewModel.AppendActivity($"Opened isolated Roblox session for {account.Label}.");
    }

    private async Task SaveAsync()
    {
        try { await _viewModel.SaveAsync(); }
        catch (Exception exception) { _viewModel.AppendActivity($"Could not save local data: {exception.Message}"); }
    }

    private static Border Card(Control child) => new() { Padding = new Thickness(18), Margin = new Thickness(0, 18, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = child };

    private async Task<(string Label, string Group)?> PromptAsync(string title, string labelValue, string groupValue)
    {
        var dialog = new Window { Title = title, Width = 420, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var label = new TextBox { Text = labelValue, PlaceholderText = "Account label" };
        var group = new TextBox { Text = groupValue, PlaceholderText = "Group (optional)" };
        var result = new TaskCompletionSource<(string, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ok = new Button { Content = "Save" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(label.Text)) { result.TrySetResult((label.Text.Trim(), group.Text?.Trim() ?? string.Empty)); dialog.Close(); } };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { label, group, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } } } };
        dialog.Show(this);
        return await result.Task;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new Window { Title = "Confirm", Width = 440, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var yes = new Button { Content = "Confirm" }; var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); }; no.Click += (_, _) => { result.TrySetResult(false); dialog.Close(); }; dialog.Closed += (_, _) => result.TrySetResult(false);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yes, no } } } };
        dialog.Show(this);
        return await result.Task;
    }
}

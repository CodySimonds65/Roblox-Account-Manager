using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class MainWindow : Window
{
    private readonly AccountStore _accountStore = new();
    private readonly GamePresetStore _gamePresetStore = new();
    private readonly SingletonService _singletonService = new();
    private readonly UpdateService _updateService = new();
    private readonly CompatibilityService _compatibilityService = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly RobloxLauncherService _robloxLauncherService = new();
    private readonly ObservableCollection<AccountProfile> _accounts = [];
    private readonly ObservableCollection<LaunchQueueItem> _launchQueue = [];
    private readonly ObservableCollection<GamePreset> _games =
    [
        new("Dungeon Quest Reborn", "https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn", true),
        new("Custom URL", "", true)
    ];

    private CoreWebView2Environment? _webEnvironment;
    private WebView2? _browser;
    private AccountProfile? _activeAccount;
    private TaskCompletionSource<bool>? _externalLaunchRequest;
    private CancellationTokenSource? _launchCancellation;
    private string? _lastLaunchUrl;
    private bool _isLaunching;
    private LauncherSettings _settings = new();
    private Point _accountDragStart;
    private bool _startupComplete;

    public MainWindow()
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        AccountsList.ItemsSource = _accounts;
        LaunchQueueList.ItemsSource = _launchQueue;
        GamePicker.ItemsSource = _games;
        GamePicker.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateService.ConfirmUpdatedLaunch(Environment.GetCommandLineArgs().Skip(1).ToArray());

        try
        {
            _settings = await _settingsStore.LoadAsync();
            await ApplyPendingDataCleanupAsync();
            ApplySettingsToControls();
            if (_settings.UpdateChecksEnabled)
            {
                _ = CheckForUpdatesAsync();
            }
            else
            {
                Log("Automatic update checks are disabled in Settings.");
            }

            var loadedAccounts = await _accountStore.LoadAsync();
            for (var index = 0; index < loadedAccounts.Count; index++)
            {
                var account = loadedAccounts[index];
                account.SortOrder = index;
                _accounts.Add(account);
            }

            foreach (var preset in await _gamePresetStore.LoadAsync())
            {
                if (!string.IsNullOrWhiteSpace(preset.Name) &&
                    GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out var normalizedUrl) &&
                    !_games.Any(game => string.Equals(game.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _games.Insert(_games.Count - 1, new GamePreset(preset.Name, normalizedUrl));
                }
            }

            ApplyRecentGameOrder();

            _webEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _accountStore.WebViewDataDirectory);

            Log("Ready. Add an account profile or select an existing one.");
            if (_accounts.Count > 0)
            {
                RestoreRememberedSelections();
            }

            RestoreRememberedGame();
            _startupComplete = true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Log("Microsoft WebView2 Runtime is not installed.");
            var answer = MessageBox.Show(
                this,
                "Microsoft WebView2 Runtime is required and was not found. Open Microsoft's official download page?",
                "WebView2 Runtime required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://developer.microsoft.com/microsoft-edge/webview2/consumer/",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception exception)
        {
            Log($"Startup error: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Roblox Account Manager startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            Log($"Checking for updates (current version {_updateService.CurrentVersion.ToString(3)})...");
            var package = await _updateService.CheckAndDownloadAsync();
            if (package is null)
            {
                Log("Roblox Account Manager is up to date.");
                return;
            }

            Log($"Update {package.Tag} downloaded and verified.");
            var answer = MessageBox.Show(
                this,
                $"Roblox Account Manager {package.Tag} is ready. Restart now to install it?",
                "Update ready",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                Log("Update postponed until the next launch.");
                return;
            }

            UpdateService.StartInstaller(package);
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            Log($"Update check skipped: {exception.Message}");
        }
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var account = new AccountProfile
        {
            Label = dialog.AccountLabel,
            Group = dialog.AccountGroup,
            SortOrder = _accounts.Count
        };
        _accounts.Add(account);
        await _accountStore.SaveAsync(_accounts);
        AccountsList.SelectedItem = account;
        Log($"Created isolated browser profile for {account.Label}. Sign in directly on Roblox's page.");
    }

    private async void EditAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountProfile account)
        {
            MessageBox.Show(this, "Select one account profile to edit.", "No profile selected");
            return;
        }

        var dialog = new InputDialog(account.Label, account.Group) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        account.Label = dialog.AccountLabel;
        account.Group = dialog.AccountGroup;
        await _accountStore.SaveAsync(_accounts);
        AccountsList.Items.Refresh();
        if (_activeAccount?.Id == account.Id)
        {
            ActiveProfileText.Text = account.Label;
        }

        Log($"Updated account profile: {account.Label}.");
    }

    private void SelectAllAccounts_Click(object sender, RoutedEventArgs e) => AccountsList.SelectAll();

    private void SelectNoAccounts_Click(object sender, RoutedEventArgs e) => AccountsList.UnselectAll();

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountProfile account })
        {
            return;
        }

        e.Handled = true;
        account.IsFavorite = !account.IsFavorite;
        ReorderAccountsForDisplay();
        await SaveAccountOrderAsync();
        AccountsList.Items.Refresh();
        AccountsList.ScrollIntoView(account);
        Log($"{(account.IsFavorite ? "Favorited" : "Unfavorited")} profile: {account.Label}.");
    }

    private void ReorderAccountsForDisplay()
    {
        var orderedAccounts = AccountStore.OrderForDisplay(_accounts);
        for (var targetIndex = 0; targetIndex < orderedAccounts.Count; targetIndex++)
        {
            var currentIndex = _accounts.IndexOf(orderedAccounts[targetIndex]);
            if (currentIndex != targetIndex)
            {
                _accounts.Move(currentIndex, targetIndex);
            }
        }
    }

    private async void MoveAccountUp_Click(object sender, RoutedEventArgs e) => await MoveSelectedAccountAsync(-1);

    private async void MoveAccountDown_Click(object sender, RoutedEventArgs e) => await MoveSelectedAccountAsync(1);

    private async Task MoveSelectedAccountAsync(int offset)
    {
        if (AccountsList.SelectedItem is not AccountProfile account)
        {
            return;
        }

        var oldIndex = _accounts.IndexOf(account);
        var newIndex = Math.Clamp(oldIndex + offset, 0, _accounts.Count - 1);
        if (newIndex == oldIndex)
        {
            return;
        }

        if (_accounts[newIndex].IsFavorite != account.IsFavorite)
        {
            return;
        }

        _accounts.Move(oldIndex, newIndex);
        AccountsList.SelectedItem = account;
        await SaveAccountOrderAsync();
    }

    private void AccountsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _accountDragStart = e.GetPosition(AccountsList);

    private void AccountsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            Math.Abs(e.GetPosition(AccountsList).Y - _accountDragStart.Y) < SystemParameters.MinimumVerticalDragDistance ||
            AccountsList.SelectedItem is not AccountProfile account)
        {
            return;
        }

        DragDrop.DoDragDrop(AccountsList, account, DragDropEffects.Move);
    }

    private async void AccountsList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AccountProfile)) || e.Data.GetData(typeof(AccountProfile)) is not AccountProfile source)
        {
            return;
        }

        var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as AccountProfile;
        if (target is null || ReferenceEquals(source, target))
        {
            return;
        }

        if (source.IsFavorite != target.IsFavorite)
        {
            return;
        }

        var oldIndex = _accounts.IndexOf(source);
        var newIndex = _accounts.IndexOf(target);
        _accounts.Move(oldIndex, newIndex);
        AccountsList.SelectedItem = source;
        await SaveAccountOrderAsync();
        Log($"Moved {source.Label} in the profile list.");
    }

    private async Task SaveAccountOrderAsync()
    {
        for (var index = 0; index < _accounts.Count; index++)
        {
            _accounts[index].SortOrder = index;
        }

        await _accountStore.SaveAsync(_accounts);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountProfile account)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove '{account.Label}' and clear its saved Roblox browser session?",
            "Remove account profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (_activeAccount?.Id == account.Id && _browser?.CoreWebView2 is not null)
        {
            await _browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            DisposeBrowser();
        }

        _accounts.Remove(account);
        await SaveAccountOrderAsync();
        _activeAccount = null;
        ActiveProfileText.Text = "No profile selected";
        BrowserPlaceholder.Visibility = Visibility.Visible;
        Log($"Removed {account.Label} and cleared its active browser session.");
    }

    private async void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountsList.SelectedItem is AccountProfile account)
        {
            await OpenAccountAsync(account);
        }

        if (_startupComplete)
        {
            await SaveRememberedStateAsync();
        }
    }

    private async Task OpenAccountAsync(AccountProfile account)
    {
        if (_webEnvironment is null || _activeAccount?.Id == account.Id)
        {
            return;
        }

        try
        {
            DisposeBrowser();
            _activeAccount = account;
            ActiveProfileText.Text = account.Label;
            BrowserPlaceholder.Visibility = Visibility.Collapsed;

            var options = _webEnvironment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = account.Id;
            options.IsInPrivateModeEnabled = false;
            _browser = new WebView2();
            BrowserHost.Children.Add(_browser);
            await _browser.EnsureCoreWebView2Async(_webEnvironment, options);
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            _browser.CoreWebView2.LaunchingExternalUriScheme += Browser_LaunchingExternalUriScheme;
            _browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _browser.CoreWebView2.Navigate(args.Uri);
            };
            _browser.CoreWebView2.Navigate("https://www.roblox.com/home");
            Log($"Opened isolated Roblox session for {account.Label}.");
        }
        catch (Exception exception)
        {
            Log($"Could not open {account.Label}: {exception.Message}");
            ActiveProfileText.Text = "Profile unavailable";
            BrowserPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void OpenLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureBrowserSelected())
        {
            return;
        }

        _browser!.CoreWebView2.Navigate("https://www.roblox.com/login");
        Log($"Opened Roblox login for {_activeAccount!.Label}. Credentials stay inside this isolated browser profile.");
    }

    private async void PrepareLaunch_Click(object sender, RoutedEventArgs e)
    {
        var selectedAccounts = AccountsList.SelectedItems.Cast<AccountProfile>().ToArray();
        if (selectedAccounts.Length == 0)
        {
            MessageBox.Show(this, "Select one or more account profiles first.", "No accounts selected");
            return;
        }

        var gameUrl = GetSelectedGameUrl();
        if (!GamePreset.TryNormalizeRobloxGameUrl(gameUrl, out gameUrl))
        {
            MessageBox.Show(this, "Enter a valid Roblox game-page URL.", "Invalid game URL");
            return;
        }

        _launchQueue.Clear();
        foreach (var account in selectedAccounts)
        {
            _launchQueue.Add(new LaunchQueueItem(account));
        }

        _lastLaunchUrl = gameUrl;
        await RunLaunchQueueAsync(_launchQueue.ToArray(), gameUrl);
        if (_launchQueue.Any(item => item.State == LaunchQueueState.Running))
        {
            await RecordRecentGameAsync();
        }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Log("Running privacy-safe compatibility diagnostics...");
            var checks = await _compatibilityService.RunAsync();
            new CompatibilityDialog(checks) { Owner = this }.ShowDialog();
            Log("Compatibility diagnostics completed.");
        }
        catch (Exception exception)
        {
            Log($"Compatibility diagnostics failed: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Diagnostics failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.ClearToolsRequested)
        {
            var toolsDirectory = Path.Combine(_accountStore.AppDataDirectory, "Tools");
            if (Directory.Exists(toolsDirectory))
            {
                Directory.Delete(toolsDirectory, recursive: true);
                Log("Cleared the downloaded Sysinternals tool cache.");
            }
        }

        if (dialog.ClearSessionsRequested)
        {
            _settings.ClearBrowserDataOnNextStart = true;
            Log("All browser sessions will be cleared on the next restart.");
        }

        ApplySettingsToControls();
        await SaveRememberedStateAsync();
        Log("Launcher settings saved.");
    }

    private async Task ApplyPendingDataCleanupAsync()
    {
        if (!_settings.ClearBrowserDataOnNextStart)
        {
            return;
        }

        if (Directory.Exists(_accountStore.WebViewDataDirectory))
        {
            Directory.Delete(_accountStore.WebViewDataDirectory, recursive: true);
        }

        _settings.ClearBrowserDataOnNextStart = false;
        await _settingsStore.SaveAsync(_settings);
        Log("Cleared all saved Roblox browser sessions.");
    }

    private void ApplySettingsToControls()
    {
        ContinueOnFailureCheckBox.IsChecked = _settings.ContinueOnFailure;
        LaunchTimeoutPicker.SelectedItem = LaunchTimeoutPicker.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _settings.LaunchTimeoutSeconds.ToString(), StringComparison.Ordinal))
            ?? LaunchTimeoutPicker.Items[1];
    }

    private void RestoreRememberedSelections()
    {
        if (!_settings.RememberSelections || _settings.LastSelectedProfileIds.Count == 0)
        {
            AccountsList.SelectedIndex = 0;
            return;
        }

        foreach (var account in _accounts.Where(account => _settings.LastSelectedProfileIds.Contains(account.Id, StringComparer.Ordinal)))
        {
            AccountsList.SelectedItems.Add(account);
        }

        if (AccountsList.SelectedItems.Count == 0)
        {
            AccountsList.SelectedIndex = 0;
        }
    }

    private void RestoreRememberedGame()
    {
        if (!_settings.RememberSelections || string.IsNullOrWhiteSpace(_settings.LastGameName))
        {
            return;
        }

        var rememberedGame = _games.FirstOrDefault(game => string.Equals(game.Name, _settings.LastGameName, StringComparison.Ordinal));
        if (rememberedGame is not null)
        {
            GamePicker.SelectedItem = rememberedGame;
        }
    }

    private async Task SaveRememberedStateAsync()
    {
        if (_settings.RememberSelections)
        {
            _settings.LastSelectedProfileIds = AccountsList.SelectedItems.Cast<AccountProfile>().Select(account => account.Id).ToList();
            _settings.LastGameName = (GamePicker.SelectedItem as GamePreset)?.Name ?? string.Empty;
        }
        else
        {
            _settings.LastSelectedProfileIds.Clear();
            _settings.LastGameName = string.Empty;
        }

        await _settingsStore.SaveAsync(_settings);
    }

    private void CancelLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_isLaunching)
        {
            Log("Cancel requested. The current Windows operation will finish before the queue stops.");
            _launchCancellation?.Cancel();
        }
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_isLaunching || string.IsNullOrWhiteSpace(_lastLaunchUrl))
        {
            return;
        }

        var failedItems = _launchQueue.Where(item => item.State == LaunchQueueState.Failed).ToArray();
        if (failedItems.Length == 0)
        {
            return;
        }

        foreach (var item in failedItems)
        {
            item.State = LaunchQueueState.Waiting;
            item.Detail = "Queued again";
        }

        await RunLaunchQueueAsync(failedItems, _lastLaunchUrl);
    }

    private async Task RunLaunchQueueAsync(IReadOnlyCollection<LaunchQueueItem> items, string gameUrl)
    {
        _launchCancellation?.Dispose();
        _launchCancellation = new CancellationTokenSource();
        var cancellationToken = _launchCancellation.Token;
        var timeout = GetLaunchTimeout();
        var continueOnFailure = ContinueOnFailureCheckBox.IsChecked == true;
        _settings.LaunchTimeoutSeconds = (int)timeout.TotalSeconds;
        _settings.ContinueOnFailure = continueOnFailure;
        await _settingsStore.SaveAsync(_settings);

        _isLaunching = true;
        PrepareLaunch_ClickButtonState(false);
        CancelLaunchButton.IsEnabled = true;
        RetryFailedButton.IsEnabled = false;

        try
        {
            Log($"Starting launch queue for {items.Count} account(s); timeout {timeout.TotalSeconds:0} seconds.");
            var firstItem = true;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!firstItem && _settings.LaunchDelaySeconds > 0)
                {
                    Log($"Waiting {_settings.LaunchDelaySeconds} seconds before the next account...");
                    await Task.Delay(TimeSpan.FromSeconds(_settings.LaunchDelaySeconds), cancellationToken);
                }

                firstItem = false;
                var (success, detail) = await LaunchAccountAsync(item, gameUrl, timeout, cancellationToken);
                item.Detail = detail;

                if (success)
                {
                    item.State = LaunchQueueState.Running;
                }
                else
                {
                    item.State = LaunchQueueState.Failed;
                    if (!continueOnFailure)
                    {
                        foreach (var remainingItem in items.Where(remaining => remaining.State == LaunchQueueState.Waiting))
                        {
                            remainingItem.State = LaunchQueueState.Canceled;
                            remainingItem.Detail = "Stopped after failure";
                        }

                        Log("Launch queue stopped after a failure.");
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var waitingItem in items.Where(item => item.State is LaunchQueueState.Waiting or LaunchQueueState.Preparing or LaunchQueueState.Launching))
            {
                waitingItem.State = LaunchQueueState.Canceled;
                waitingItem.Detail = "Canceled";
            }

            Log("Launch queue canceled.");
        }
        finally
        {
            var running = _launchQueue.Count(item => item.State == LaunchQueueState.Running);
            var failed = _launchQueue.Count(item => item.State == LaunchQueueState.Failed);
            Log($"Launch queue finished: {running} running, {failed} failed.");
            _isLaunching = false;
            PrepareLaunch_ClickButtonState(true);
            CancelLaunchButton.IsEnabled = false;
            RetryFailedButton.IsEnabled = failed > 0;
        }
    }

    private async Task<(bool Success, string Detail)> LaunchAccountAsync(
        LaunchQueueItem item,
        string gameUrl,
        TimeSpan processTimeout,
        CancellationToken cancellationToken)
    {
        item.State = LaunchQueueState.Preparing;
        item.Detail = "Opening session";
        await OpenAccountAsync(item.Account);
        cancellationToken.ThrowIfCancellationRequested();

        if (_browser?.CoreWebView2 is null || _activeAccount?.Id != item.Account.Id)
        {
            Log($"Skipped {item.Label}: its browser profile could not be opened.");
            return (false, "Session unavailable");
        }

        var previousProcessCount = Process.GetProcessesByName("RobloxPlayerBeta").Length;
        if (previousProcessCount > 0)
        {
            item.Detail = "Releasing singleton";
            Log($"Preparing {item.Label}: requesting administrator approval to release Roblox singleton handles...");
            var result = await _singletonService.ReleaseAsync();
            foreach (var message in result.Messages)
            {
                Log(message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                Log($"Could not prepare {item.Label}: singleton release failed.");
                return (false, "Singleton release failed");
            }
        }
        else
        {
            Log($"Launching {item.Label} as the first Roblox client.");
        }

        item.State = LaunchQueueState.Launching;
        item.Detail = "Requesting Roblox";
        if (!await NavigateAndAutoLaunchAsync(gameUrl, cancellationToken))
        {
            Log($"{item.Label} did not produce a Roblox launch request.");
            return (false, "No launch request");
        }

        item.Detail = "Waiting for process";
        if (!await WaitForAdditionalRobloxProcessAsync(previousProcessCount, processTimeout, cancellationToken))
        {
            Log($"No new Roblox process appeared for {item.Label} within {processTimeout.TotalSeconds:0} seconds.");
            return (false, "Process timed out");
        }

        Log($"{item.Label} is running.");
        return (true, "Roblox started");
    }

    private TimeSpan GetLaunchTimeout()
    {
        if (LaunchTimeoutPicker.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(45);
    }

    private async void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustom = GamePicker.SelectedItem is GamePreset game && string.IsNullOrEmpty(game.Url);
        CustomUrlBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        RemoveGamePresetButton.IsEnabled = GamePicker.SelectedItem is GamePreset { IsBuiltIn: false };
        EditGamePresetButton.IsEnabled = GamePicker.SelectedItem is GamePreset { IsBuiltIn: false };
        DuplicateGamePresetButton.IsEnabled = GamePicker.SelectedItem is GamePreset { Url.Length: > 0 };
        if (!isCustom && GamePicker.SelectedItem is GamePreset selectedGame)
        {
            PresetHintText.Text = $"Ready to launch {selectedGame.Name}";
        }

        if (_startupComplete)
        {
            await SaveRememberedStateAsync();
        }
    }

    private async void AddGamePreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GamePresetDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_games.Any(game => string.Equals(game.Name, dialog.GameName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A preset with that game name already exists.", "Duplicate preset");
            return;
        }

        if (_games.Any(game => !string.IsNullOrEmpty(game.Url) &&
                               string.Equals(game.Url, dialog.GameUrl, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That Roblox game URL is already saved as a preset.", "Duplicate preset");
            return;
        }

        var preset = new GamePreset(dialog.GameName, dialog.GameUrl);
        _games.Insert(_games.Count - 1, preset);
        await SaveCustomGamePresetsAsync();
        GamePicker.SelectedItem = preset;
        Log($"Added game preset: {preset.Name}.");
    }

    private async void EditGamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (GamePicker.SelectedItem is not GamePreset { IsBuiltIn: false } preset)
        {
            return;
        }

        var dialog = new GamePresetDialog(preset) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_games.Any(game => !ReferenceEquals(game, preset) &&
                               (string.Equals(game.Name, dialog.GameName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(game.Url, dialog.GameUrl, StringComparison.OrdinalIgnoreCase))))
        {
            MessageBox.Show(this, "Another preset already uses that name or URL.", "Duplicate preset");
            return;
        }

        var index = _games.IndexOf(preset);
        var updated = new GamePreset(dialog.GameName, dialog.GameUrl);
        _games[index] = updated;
        var recentIndex = _settings.RecentGameNames.FindIndex(name => string.Equals(name, preset.Name, StringComparison.Ordinal));
        if (recentIndex >= 0)
        {
            _settings.RecentGameNames[recentIndex] = updated.Name;
        }

        await SaveCustomGamePresetsAsync();
        await _settingsStore.SaveAsync(_settings);
        GamePicker.SelectedItem = updated;
        Log($"Updated game preset: {updated.Name}.");
    }

    private async void DuplicateGamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (GamePicker.SelectedItem is not GamePreset { Url.Length: > 0 } preset)
        {
            return;
        }

        var baseName = $"{preset.Name} copy";
        var name = baseName;
        for (var suffix = 2; _games.Any(game => string.Equals(game.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            name = $"{baseName} {suffix}";
        }

        var duplicate = new GamePreset(name, preset.Url);
        _games.Insert(_games.Count - 1, duplicate);
        await SaveCustomGamePresetsAsync();
        GamePicker.SelectedItem = duplicate;
        Log($"Duplicated game preset: {duplicate.Name}.");
    }

    private async void ImportGamePresets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Roblox game presets",
            Filter = "Roblox Account Manager presets (*.json)|*.json|JSON files (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await PresetTransferService.ImportAsync(dialog.FileName);
            var added = 0;
            foreach (var preset in imported)
            {
                if (_games.Any(existing =>
                        string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.Url, preset.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _games.Insert(_games.Count - 1, preset);
                added++;
            }

            await SaveCustomGamePresetsAsync();
            Log($"Imported {added} game preset(s). Existing names and URLs were skipped.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Preset import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportGamePresets_Click(object sender, RoutedEventArgs e)
    {
        var customPresets = _games.Where(game => !game.IsBuiltIn).ToArray();
        if (customPresets.Length == 0)
        {
            MessageBox.Show(this, "There are no custom presets to export.", "No presets");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Roblox game presets",
            Filter = "Roblox Account Manager presets (*.json)|*.json",
            FileName = "roblox-game-presets.json",
            AddExtension = true,
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await PresetTransferService.ExportAsync(dialog.FileName, customPresets);
        Log($"Exported {customPresets.Length} custom game preset(s). No account or session data was included.");
    }

    private void PresetSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = PresetSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_games);
        view.Filter = item => item is GamePreset preset &&
                              (query.Length == 0 || preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        view.Refresh();
    }

    private void ApplyRecentGameOrder()
    {
        foreach (var name in _settings.RecentGameNames.AsEnumerable().Reverse())
        {
            var game = _games.FirstOrDefault(candidate =>
                !string.IsNullOrEmpty(candidate.Url) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (game is null)
            {
                continue;
            }

            _games.Move(_games.IndexOf(game), 0);
        }
    }

    private async Task RecordRecentGameAsync()
    {
        if (GamePicker.SelectedItem is not GamePreset { Url.Length: > 0 } game)
        {
            return;
        }

        _settings.RecentGameNames.RemoveAll(name => string.Equals(name, game.Name, StringComparison.Ordinal));
        _settings.RecentGameNames.Insert(0, game.Name);
        if (_settings.RecentGameNames.Count > 5)
        {
            _settings.RecentGameNames.RemoveRange(5, _settings.RecentGameNames.Count - 5);
        }

        await _settingsStore.SaveAsync(_settings);
        ApplyRecentGameOrder();
        GamePicker.SelectedItem = game;
    }

    private async void RemoveGamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (GamePicker.SelectedItem is not GamePreset { IsBuiltIn: false } preset)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove the '{preset.Name}' game preset?",
            "Remove game preset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _games.Remove(preset);
        _settings.RecentGameNames.RemoveAll(name => string.Equals(name, preset.Name, StringComparison.Ordinal));
        await SaveCustomGamePresetsAsync();
        await _settingsStore.SaveAsync(_settings);
        GamePicker.SelectedIndex = 0;
        Log($"Removed game preset: {preset.Name}.");
    }

    private Task SaveCustomGamePresetsAsync() =>
        _gamePresetStore.SaveAsync(_games.Where(game => !game.IsBuiltIn));

    private string GetSelectedGameUrl() => GamePicker.SelectedItem is GamePreset game && !string.IsNullOrEmpty(game.Url)
        ? game.Url
        : CustomUrlBox.Text.Trim();

    private async Task<bool> NavigateAndAutoLaunchAsync(string gameUrl, CancellationToken cancellationToken)
    {
        var browser = _browser!;
        var navigationFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (browser.Source?.Scheme == Uri.UriSchemeHttps)
            {
                navigationFinished.TrySetResult(args.IsSuccess);
            }
        }

        browser.CoreWebView2.NavigationCompleted += NavigationCompleted;
        try
        {
            browser.CoreWebView2.Navigate(gameUrl);
            var completed = await Task.WhenAny(navigationFinished.Task, Task.Delay(TimeSpan.FromSeconds(20), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != navigationFinished.Task || !await navigationFinished.Task)
            {
                Log("The Roblox game page did not finish loading. You can still use its Play button manually.");
                return false;
            }
        }
        finally
        {
            browser.CoreWebView2.NavigationCompleted -= NavigationCompleted;
        }

        _externalLaunchRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = false;
        for (var attempt = 0; attempt < 30 && ReferenceEquals(browser, _browser); attempt++)
        {
            var result = await browser.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    const button = document.querySelector('button[data-testid="play-button"]');
                    if (!button || button.disabled || button.getAttribute('aria-disabled') === 'true') {
                        return false;
                    }
                    button.click();
                    return true;
                })();
                """);

            if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
            {
                clicked = true;
                Log($"Activated Roblox's Play button for {_activeAccount!.Label}.");
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        if (!clicked)
        {
            Log("Roblox's Play button was not available. Confirm this account is signed in, then try again.");
            return false;
        }

        var launchResult = await Task.WhenAny(_externalLaunchRequest.Task, Task.Delay(TimeSpan.FromSeconds(12), cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (launchResult == _externalLaunchRequest.Task && await _externalLaunchRequest.Task)
        {
            Log($"Launch request sent for {_activeAccount!.Label}.");
            return true;
        }
        else if (browser.Source?.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) == true)
        {
            Log($"{_activeAccount!.Label} is not signed in. Complete Roblox login and try again.");
            return false;
        }
        else
        {
            Log("Roblox did not produce a player launch request. Use the visible Play button as a fallback.");
            return false;
        }
    }

    private static async Task<bool> WaitForAdditionalRobloxProcessAsync(
        int previousCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken);
            if (Process.GetProcessesByName("RobloxPlayerBeta").Length > previousCount)
            {
                return true;
            }
        }

        return false;
    }

    private void Browser_LaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        args.Cancel = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var launchUri) ||
            !IsRobloxPlayerScheme(launchUri.Scheme) ||
            !IsTrustedRobloxOrigin(args.InitiatingOrigin))
        {
            Log("Blocked an untrusted external protocol request from the embedded browser.");
            _externalLaunchRequest?.TrySetResult(false);
            return;
        }

        try
        {
            _robloxLauncherService.Start(args.Uri, _settings.PreferredLauncher);
            _externalLaunchRequest?.TrySetResult(true);
        }
        catch (Exception exception)
        {
            Log($"Windows could not start Roblox: {exception.Message}");
            _externalLaunchRequest?.TrySetResult(false);
        }
    }

    private static bool IsRobloxPlayerScheme(string scheme) =>
        string.Equals(scheme, "roblox-player", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "roblox", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedRobloxOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
    }

    private bool EnsureBrowserSelected()
    {
        if (_browser?.CoreWebView2 is not null && _activeAccount is not null)
        {
            return true;
        }

        MessageBox.Show(this, "Add or select an account profile first.", "No account selected");
        return false;
    }

    private void PrepareLaunch_ClickButtonState(bool enabled)
    {
        AutoLaunchButton.IsEnabled = enabled;
        AccountsList.IsEnabled = enabled;
        GamePicker.IsEnabled = enabled;
        CustomUrlBox.IsEnabled = enabled;
        AddGamePresetButton.IsEnabled = enabled;
        RemoveGamePresetButton.IsEnabled = enabled && GamePicker.SelectedItem is GamePreset { IsBuiltIn: false };
        EditGamePresetButton.IsEnabled = enabled && GamePicker.SelectedItem is GamePreset { IsBuiltIn: false };
        DuplicateGamePresetButton.IsEnabled = enabled && GamePicker.SelectedItem is GamePreset { Url.Length: > 0 };
        PresetSearchBox.IsEnabled = enabled;
        AddAccountButton.IsEnabled = enabled;
        RemoveAccountButton.IsEnabled = enabled;
        OpenLoginButton.IsEnabled = enabled;
        LaunchProgress.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        LaunchTimeoutPicker.IsEnabled = enabled;
        ContinueOnFailureCheckBox.IsEnabled = enabled;
    }

    private void DisposeBrowser()
    {
        if (_browser is null)
        {
            return;
        }

        BrowserHost.Children.Remove(_browser);
        _browser.Dispose();
        _browser = null;
    }

    private void Log(string message)
    {
        ActivityLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        ActivityLog.ScrollToEnd();
    }

    private void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ActivityLog.Text))
        {
            Clipboard.SetText(ActivityLog.Text);
            Log("Activity log copied to the clipboard.");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _launchCancellation?.Cancel();
        _launchCancellation?.Dispose();
        DisposeBrowser();
        base.OnClosed(e);
    }
}

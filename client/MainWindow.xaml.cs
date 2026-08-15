using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class MainWindow : Window
{
    private readonly AccountStore _accountStore = new();
    private readonly GamePresetStore _gamePresetStore = new();
    private readonly SingletonService _singletonService = new();
    private readonly ObservableCollection<AccountProfile> _accounts = [];
    private readonly ObservableCollection<GamePreset> _games =
    [
        new("Dungeon Quest Reborn", "https://www.roblox.com/games/77649408247578/Dungeon-Quest-Reborn", true),
        new("Custom URL", "", true)
    ];

    private CoreWebView2Environment? _webEnvironment;
    private WebView2? _browser;
    private AccountProfile? _activeAccount;
    private TaskCompletionSource<bool>? _externalLaunchRequest;

    public MainWindow()
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        AccountsList.ItemsSource = _accounts;
        GamePicker.ItemsSource = _games;
        GamePicker.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var account in await _accountStore.LoadAsync())
            {
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

            _webEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _accountStore.WebViewDataDirectory);

            Log("Ready. Add an account profile or select an existing one.");
            if (_accounts.Count > 0)
            {
                AccountsList.SelectedIndex = 0;
            }
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
            MessageBox.Show(this, exception.Message, "Roblox Alt Client startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var account = new AccountProfile { Label = dialog.AccountLabel };
        _accounts.Add(account);
        await _accountStore.SaveAsync(_accounts);
        AccountsList.SelectedItem = account;
        Log($"Created isolated browser profile for {account.Label}. Sign in directly on Roblox's page.");
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
        await _accountStore.SaveAsync(_accounts);
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

        PrepareLaunch_ClickButtonState(false);
        try
        {
            Log($"Starting multi-launch for {selectedAccounts.Length} selected account(s).");
            var launchedCount = 0;

            foreach (var account in selectedAccounts)
            {
                await OpenAccountAsync(account);
                if (_browser?.CoreWebView2 is null || _activeAccount?.Id != account.Id)
                {
                    Log($"Skipped {account.Label}: its browser profile could not be opened.");
                    continue;
                }

                var previousProcessCount = Process.GetProcessesByName("RobloxPlayerBeta").Length;
                if (previousProcessCount > 0)
                {
                    Log($"Preparing {account.Label}: requesting administrator approval to release Roblox singleton handles...");
                    var result = await _singletonService.ReleaseAsync();
                    foreach (var message in result.Messages)
                    {
                        Log(message);
                    }

                    if (!result.Success)
                    {
                        Log($"Stopped before {account.Label} because the singleton release failed.");
                        break;
                    }
                }
                else
                {
                    Log($"Launching {account.Label} as the first Roblox client.");
                }

                var requestSent = await NavigateAndAutoLaunchAsync(gameUrl);
                if (!requestSent)
                {
                    Log($"Stopped because {account.Label} did not produce a Roblox launch request.");
                    break;
                }

                if (await WaitForAdditionalRobloxProcessAsync(previousProcessCount, TimeSpan.FromSeconds(45)))
                {
                    launchedCount++;
                    Log($"{account.Label} is running ({launchedCount}/{selectedAccounts.Length} launched)." );
                }
                else
                {
                    Log($"Stopped: no new Roblox process appeared for {account.Label} within 45 seconds.");
                    break;
                }
            }

            Log($"Multi-launch finished: {launchedCount} of {selectedAccounts.Length} selected account(s) started.");
        }
        finally
        {
            PrepareLaunch_ClickButtonState(true);
        }
    }

    private void GamePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isCustom = GamePicker.SelectedItem is GamePreset game && string.IsNullOrEmpty(game.Url);
        CustomUrlBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
        RemoveGamePresetButton.IsEnabled = GamePicker.SelectedItem is GamePreset { IsBuiltIn: false };
        if (!isCustom && GamePicker.SelectedItem is GamePreset selectedGame)
        {
            PresetHintText.Text = $"Ready to launch {selectedGame.Name}";
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
        await SaveCustomGamePresetsAsync();
        GamePicker.SelectedIndex = 0;
        Log($"Removed game preset: {preset.Name}.");
    }

    private Task SaveCustomGamePresetsAsync() =>
        _gamePresetStore.SaveAsync(_games.Where(game => !game.IsBuiltIn));

    private string GetSelectedGameUrl() => GamePicker.SelectedItem is GamePreset game && !string.IsNullOrEmpty(game.Url)
        ? game.Url
        : CustomUrlBox.Text.Trim();

    private async Task<bool> NavigateAndAutoLaunchAsync(string gameUrl)
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
            var completed = await Task.WhenAny(navigationFinished.Task, Task.Delay(TimeSpan.FromSeconds(20)));
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

            await Task.Delay(500);
        }

        if (!clicked)
        {
            Log("Roblox's Play button was not available. Confirm this account is signed in, then try again.");
            return false;
        }

        var launchResult = await Task.WhenAny(_externalLaunchRequest.Task, Task.Delay(TimeSpan.FromSeconds(12)));
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

    private static async Task<bool> WaitForAdditionalRobloxProcessAsync(int previousCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
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
            Process.Start(new ProcessStartInfo
            {
                FileName = args.Uri,
                UseShellExecute = true
            });
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
        AddAccountButton.IsEnabled = enabled;
        RemoveAccountButton.IsEnabled = enabled;
        OpenLoginButton.IsEnabled = enabled;
        LaunchProgress.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
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
        DisposeBrowser();
        base.OnClosed(e);
    }
}

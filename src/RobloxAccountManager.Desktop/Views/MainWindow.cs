using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using System.Collections.ObjectModel;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Data;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Core.Models;
using RobloxAccountManager.Desktop.Services;
using RobloxAccountManager.Desktop.ViewModels;
using RobloxAccountManager.Platform.MacOS;
using CoreClientWindowManager = RobloxAccountManager.Core.Contracts.IClientWindowManager;
using CoreMacLaunchLevel = RobloxAccountManager.Core.Contracts.MacLaunchLevel;
using CoreRobloxLaunchRequest = RobloxAccountManager.Core.Contracts.RobloxLaunchRequest;
using CoreRobloxProcessInfo = RobloxAccountManager.Core.Contracts.RobloxProcessInfo;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace RobloxAccountManager.Desktop.Views;

public sealed class MainWindow : Window
{
    private static readonly SolidColorBrush AppBackgroundBrush = new(Avalonia.Media.Color.Parse("#090B10"));
    private static readonly SolidColorBrush SurfaceBrush = new(Avalonia.Media.Color.Parse("#11141B"));
    private static readonly SolidColorBrush ElevatedBrush = new(Avalonia.Media.Color.Parse("#171B24"));
    private static readonly SolidColorBrush HoverBrush = new(Avalonia.Media.Color.Parse("#1D2230"));
    private static readonly SolidColorBrush ControlBorderBrush = new(Avalonia.Media.Color.Parse("#272D3A"));
    private static readonly SolidColorBrush TextBrush = new(Avalonia.Media.Color.Parse("#F5F7FA"));
    private static readonly SolidColorBrush MutedTextBrush = new(Avalonia.Media.Color.Parse("#929AAD"));
    private static readonly SolidColorBrush AccentBrush = new(Avalonia.Media.Color.Parse("#7C5CFC"));
    private static readonly SolidColorBrush AccentHoverBrush = new(Avalonia.Media.Color.Parse("#8C70FF"));
    private static readonly SolidColorBrush DangerBrush = new(Avalonia.Media.Color.Parse("#281820"));
    private static readonly SolidColorBrush DangerTextBrush = new(Avalonia.Media.Color.Parse("#FF9BAA"));
    private static readonly SolidColorBrush SuccessBrush = new(Avalonia.Media.Color.Parse("#35D399"));
    private static readonly SolidColorBrush InputBrush = new(Avalonia.Media.Color.Parse("#0D1016"));
    private static readonly SolidColorBrush SelectionSurfaceBrush = new(Avalonia.Media.Color.Parse("#2A234D"));
    private static readonly SolidColorBrush SelectionBorderBrush = new(Avalonia.Media.Color.Parse("#544394"));
    private readonly DesktopShellViewModel _viewModel;
    private readonly ContentControl _content = new();
    private readonly TextBlock _pageTitle = new();
    private readonly TextBlock _pageDescription = new();
    private readonly TextBox _activity = new();
    private readonly ContentControl _browserHost = new();
    private readonly ComboBox _activityTimeout = new();
    private readonly CheckBox _continueOnFailure = new();
    private readonly Button _cancelLaunch = new();
    private readonly Button _retryFailed = new();
    private readonly Button _copyActivity = new();
    private Button? _showPresets;
    private Button? _launchSelected;
    private Button? _queueLaunch;
    private readonly StackPanel _queueSummary = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
    private readonly ListBox _accountsRail = new();
    private readonly Dictionary<string, Border> _accountCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _accountChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Window> _utilityWindows = new(StringComparer.Ordinal);
    private readonly AvaloniaAccountBrowserSessionService _browserSessions;
    private readonly SerializedLaunchCoordinator? _launches;
    private readonly CoreClientWindowManager? _clients;
    private readonly MacClientOverlayManager? _clientOverlay;
    private readonly IPlatformUpdateSource? _updateSource;
    private readonly DesktopValidationMode _validationMode;
    private CancellationTokenSource? _launchCancellation;
    private string? _lastGameUrl;
    private GamePreset? _lastLaunchPreset;
    private string? _lastCustomUrl;
    private bool _suppressAccountSelection;
    private bool _updateCheckStarted;
    private long _browserActivationVersion;
    private readonly HashSet<string> _queueSelectedAccounts = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ClientTabItem> _clientTabs = [];
    private readonly Avalonia.Threading.DispatcherTimer? _clientOverlayTimer;
    private ListBox? _clientTabsControl;
    private Border? _clientViewport;
    private TextBlock? _clientOverlayStatus;
    private string? _selectedClientAccountId;
    private bool _clientViewVisible;
    private bool _clientRefreshInProgress;
    private bool _suppressClientSelection;
    private CancellationTokenSource? _clientOverlayActivation;
    private long _clientOverlayGeneration;
    private volatile bool _launcherIsActive;
    private readonly SemaphoreSlim _pageNavigationGate = new(1, 1);

    public MainWindow(
        DesktopShellViewModel viewModel,
        AvaloniaAccountBrowserSessionService browserSessions,
        SerializedLaunchCoordinator? launches = null,
        CoreClientWindowManager? clients = null,
        MacClientOverlayManager? clientOverlay = null,
        IPlatformUpdateSource? updateSource = null,
        DesktopValidationMode validationMode = DesktopValidationMode.None)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _browserSessions = browserSessions ?? throw new ArgumentNullException(nameof(browserSessions));
        _launches = launches;
        _clients = clients;
        _clientOverlay = clientOverlay;
        _updateSource = updateSource;
        _validationMode = validationMode;
        _browserSessions.NavigationDiagnostic += AppendNavigationDiagnostic;
        Title = "Roblox Account Manager";
        Width = 1380;
        Height = 860;
        MinWidth = 1080;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = AppBackgroundBrush;
        Foreground = TextBrush;

        if (_clientOverlay is not null)
        {
            _clientOverlayTimer = new Avalonia.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(500),
                Avalonia.Threading.DispatcherPriority.Background,
                async (_, _) => await RefreshClientOverlayAsync());
        }

        _pageTitle.FontSize = 28;
        _pageTitle.FontWeight = FontWeight.Bold;
        _pageTitle.Foreground = TextBrush;
        _pageDescription.TextWrapping = TextWrapping.Wrap;
        _pageDescription.Foreground = MutedTextBrush;
        _activity.IsReadOnly = true;
        _activity.AcceptsReturn = true;
        _activity.TextWrapping = TextWrapping.NoWrap;
        _activity.MinHeight = 0;
        _activity.VerticalAlignment = VerticalAlignment.Stretch;
        _activity.ClipToBounds = true;
        ScrollViewer.SetHorizontalScrollBarVisibility(_activity, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_activity, ScrollBarVisibility.Auto);
        _activity.Background = InputBrush;
        _activity.Foreground = TextBrush;
        _activity.BorderBrush = ControlBorderBrush;
        _activity.BorderThickness = new Thickness(1);
        _activity.Padding = new Thickness(10, 8);
        _activity.FontSize = 12;

        var header = BuildWorkspaceHeader();

        var page = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(1.2, GridUnitType.Star) { MinHeight = 160 },
                new RowDefinition(6, GridUnitType.Pixel),
                new RowDefinition(1, GridUnitType.Star) { MinHeight = 130 }
            },
            RowSpacing = 14,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        page.Children.Add(header);
        Grid.SetRow(_content, 1);
        _content.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _content.VerticalContentAlignment = VerticalAlignment.Stretch;
        page.Children.Add(_content);
        var activitySplitter = new GridSplitter
        {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = ControlBorderBrush,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = false,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth)
        };
        Grid.SetRow(activitySplitter, 2);
        page.Children.Add(activitySplitter);
        var activityCard = BuildActivityCard();
        Grid.SetRow(activityCard, 3);
        page.Children.Add(activityCard);

        var workspace = new Border
        {
            Background = AppBackgroundBrush,
            Padding = new Thickness(0, 0, 2, 0),
            Child = page
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("286,18,*"),
            Margin = new Thickness(18),
            Background = AppBackgroundBrush
        };
        grid.Children.Add(BuildSidebar());
        Grid.SetColumn(workspace, 2);
        grid.Children.Add(workspace);
        Content = grid;
        Opened += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            _clientOverlayTimer?.Stop();
            _clientOverlayActivation?.Cancel();
            _clientOverlayActivation?.Dispose();
            _clientOverlayActivation = null;
            foreach (var utilityWindow in _utilityWindows.Values.ToArray()) utilityWindow.Close();
            _utilityWindows.Clear();
        };
        Closing += (_, _) =>
        {
            if (_clientOverlay is null) return;
            InvalidateClientOverlay();
            try { _clientOverlay.RestoreAllAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* Window shutdown must continue after best-effort restoration. */ }
        };
        PropertyChanged += async (_, args) =>
        {
            if (args.Property != WindowStateProperty || _clientOverlay is null) return;
            if (WindowState == WindowState.Minimized)
            {
                _ = await DeactivateClientOverlayAsync();
            }
            else if (string.Equals(_viewModel.SelectedPage.Key, "clients", StringComparison.Ordinal))
                RenderPage();
        };
        Activated += (_, _) => _launcherIsActive = true;
        Deactivated += (_, _) => _launcherIsActive = false;
    }

    private Control BuildWorkspaceHeader()
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 3, 2, 0),
            ColumnSpacing = 18
        };
        var copy = new StackPanel { Spacing = 3 };
        copy.Children.Add(_pageTitle);
        copy.Children.Add(_pageDescription);
        copy.Children.Add(new TextBlock { Text = _viewModel.PlatformLabel, FontSize = 11, Foreground = MutedTextBrush });
        header.Children.Add(copy);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _showPresets = new Button { Content = "Show presets", Padding = new Thickness(13, 7), IsVisible = false };
        StyleButton(_showPresets, secondary: true);
        _showPresets.Click += async (_, _) =>
        {
            _viewModel.Settings.ShowGamePresetPanel = true;
            await SaveAsync();
            SelectPage("accounts");
        };
        actions.Children.Add(_showPresets);
        AddHeaderAction(actions, "Plugins", "plugins");
        AddHeaderAction(actions, "Settings", "settings");
        AddHeaderAction(actions, "Diagnostics", "diagnostics");
        var localOnly = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#10261F")),
            BorderBrush = new SolidColorBrush(Avalonia.Media.Color.Parse("#1C4B3C")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new Ellipse { Width = 7, Height = 7, Fill = SuccessBrush, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "LOCAL ONLY", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#7DE6BC")) }
                }
            }
        };
        actions.Children.Add(localOnly);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        UpdatePresetRevealButton();
        return header;
    }

    private void UpdatePresetRevealButton()
    {
        if (_showPresets is null) return;
        _showPresets.IsVisible = !_viewModel.Settings.ShowGamePresetPanel || _viewModel.SelectedPage.Key == "clients";
    }

    private void AddHeaderAction(Panel panel, string title, string pageKey)
    {
        var button = new Button { Content = title, Padding = new Thickness(13, 7) };
        StyleButton(button, secondary: true);
        button.Click += (_, _) => OpenUtilityWindow(pageKey);
        panel.Children.Add(button);
    }

    private void OpenUtilityWindow(string pageKey)
    {
        if (_utilityWindows.TryGetValue(pageKey, out var existing))
        {
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var page = _viewModel.Pages.FirstOrDefault(candidate => string.Equals(candidate.Key, pageKey, StringComparison.Ordinal));
        if (page is null) return;

        Window? window = null;
        void Refresh()
        {
            if (window is not null) window.Content = BuildUtilityWindowContent(page, Refresh);
        }

        window = new Window
        {
            Title = $"{page.Title} — Roblox Account Manager",
            Width = pageKey == "settings" ? 1120 : 920,
            Height = pageKey == "settings" ? 780 : 680,
            MinWidth = pageKey == "settings" ? 920 : 720,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppBackgroundBrush,
            Foreground = TextBrush
        };
        window.Closed += (_, _) => _utilityWindows.Remove(pageKey);
        window.Content = BuildUtilityWindowContent(page, Refresh);
        _utilityWindows[pageKey] = window;
        window.Show(this);
        window.Activate();
    }

    private Control BuildUtilityWindowContent(NavigationItemViewModel page, Action refresh)
    {
        var header = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = page.Title, FontSize = 25, FontWeight = FontWeight.Bold });
        header.Children.Add(new TextBlock { Text = page.Description, Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        var content = page.Key switch
        {
            "settings" => BuildSettingsPage(),
            "plugins" => BuildPluginsPage(refresh),
            "diagnostics" => BuildDiagnosticsPage(),
            _ => new TextBlock { Text = "This utility window is unavailable." }
        };
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 0, Children = { header, scroll } };
        Grid.SetRow(scroll, 1);
        return new Border { Background = AppBackgroundBrush, Padding = new Thickness(22), Child = layout };
    }

    private Border BuildSidebar()
    {
        _accountsRail.ItemsSource = _viewModel.Accounts;
        _accountsRail.MinHeight = 120;
        _accountsRail.VerticalAlignment = VerticalAlignment.Stretch;
        _accountsRail.Background = Brushes.Transparent;
        _accountsRail.BorderThickness = new Thickness(0);
        _accountsRail.SelectionMode = SelectionMode.Multiple;
        _accountsRail.ItemTemplate = AccountRailTemplatePolicy.CreateTemplate(BuildAccountRailRow);
        _accountsRail.SelectionChanged += async (_, args) =>
        {
            if (_suppressAccountSelection) return;
            foreach (var added in args.AddedItems.OfType<AccountProfile>()) _queueSelectedAccounts.Add(added.Id);
            foreach (var removed in args.RemovedItems.OfType<AccountProfile>()) _queueSelectedAccounts.Remove(removed.Id);
            await SaveAsync();
            if (_accountsRail.SelectedItem is not AccountProfile account)
            {
                if (_accountsRail.SelectedItems is null || _accountsRail.SelectedItems.Count == 0)
                {
                    _viewModel.SelectedAccount = null;
                    Interlocked.Increment(ref _browserActivationVersion);
                    UpdateAccountSelectionVisuals();
                    RenderPage();
                }
                return;
            }
            _viewModel.SelectedAccount = account;
            UpdateAccountSelectionVisuals();
            SelectPage("accounts");
            await OpenAccountAsync(account);
        };

        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };
        var brand = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*"), Margin = new Thickness(2, 1, 2, 25) };
        brand.Children.Add(new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(11),
            Background = AccentBrush,
            Child = new TextBlock { Text = "R", FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var brandText = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        brandText.Children.Add(new TextBlock { Text = "Account Manager", FontSize = 18, FontWeight = FontWeight.SemiBold });
        brandText.Children.Add(new TextBlock { Text = "Roblox launcher", FontSize = 12, Foreground = MutedTextBrush });
        Grid.SetColumn(brandText, 1);
        brand.Children.Add(brandText);
        Grid.SetRow(brand, 0);
        shell.Children.Add(brand);

        var profilesHeader = new Grid { Margin = new Thickness(2, 0, 2, 9) };
        profilesHeader.Children.Add(new TextBlock { Text = "ACCOUNT PROFILES", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = MutedTextBrush });
        profilesHeader.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#211D38")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2),
            Child = new TextBlock { Text = "MULTI-SELECT", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#B5A7FF")) }
        });
        Grid.SetRow(profilesHeader, 1);
        shell.Children.Add(profilesHeader);
        Grid.SetRow(_accountsRail, 2);
        shell.Children.Add(_accountsRail);

        var accountActions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        var add = new Button { Content = "＋  Add account profile", HorizontalContentAlignment = HorizontalAlignment.Center };
        StyleButton(add);
        add.Click += async (_, _) =>
        {
            var values = await PromptAsync("New account profile", "Roblox account", string.Empty, false);
            if (values is null) return;
            _viewModel.Accounts.Add(new AccountProfile
            {
                Label = values.Value.Label,
                Group = values.Value.Group,
                EmbedInClients = values.Value.ShowInClients,
                SortOrder = _viewModel.Accounts.Count
            });
            await SaveAsync();
            RenderPage();
        };
        accountActions.Children.Add(add);

        var edit = new Button { Content = "Edit profile" };
        StyleButton(edit, secondary: true);
        edit.Click += async (_, _) =>
        {
            if (_viewModel.SelectedAccount is null) return;
            var account = _viewModel.SelectedAccount;
            var values = await PromptAsync("Edit account profile", account.Label, account.Group, account.EmbedInClients);
            if (values is null) return;
            account.Label = values.Value.Label;
            account.Group = values.Value.Group;
            account.EmbedInClients = values.Value.ShowInClients;
            await SaveAsync();
            RenderPage();
        };
        var reorder = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        reorder.Children.Add(edit);
        var moveUp = new Button { Content = "↑", Width = 34, Padding = new Thickness(6, 5) };
        StyleButton(moveUp, secondary: true);
        moveUp.Click += async (_, _) => await MoveSelectedAccountAsync(-1);
        Grid.SetColumn(moveUp, 1);
        reorder.Children.Add(moveUp);
        var moveDown = new Button { Content = "↓", Width = 34, Padding = new Thickness(6, 5) };
        StyleButton(moveDown, secondary: true);
        moveDown.Click += async (_, _) => await MoveSelectedAccountAsync(1);
        Grid.SetColumn(moveDown, 2);
        reorder.Children.Add(moveDown);
        accountActions.Children.Add(reorder);

        var remove = new Button { Content = "Remove selected session" };
        StyleButton(remove, danger: true);
        remove.Click += async (_, _) =>
        {
            if (_viewModel.SelectedAccount is null || !await ConfirmAsync($"Remove '{_viewModel.SelectedAccount.Label}' and clear its browser session?")) return;
            var account = _viewModel.SelectedAccount;
            try { await _browserSessions.RemoveAsync(account.Id); }
            catch (Exception exception) { _viewModel.AppendActivity($"Browser data cleanup skipped: {exception.Message}"); }
            _viewModel.Accounts.Remove(account);
            _viewModel.SelectedAccount = null;
            await SaveAsync();
            RenderPage();
        };
        accountActions.Children.Add(remove);
        var selectionActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        var selectAll = new Button { Content = "Select all", Padding = new Thickness(10, 6) };
        StyleButton(selectAll, secondary: true);
        selectAll.Click += async (_, _) =>
        {
            foreach (var account in _viewModel.Accounts) _queueSelectedAccounts.Add(account.Id);
            if (_viewModel.SelectedAccount is null && _viewModel.Accounts.Count > 0)
                _viewModel.SelectedAccount = _viewModel.Accounts[0];
            _suppressAccountSelection = true;
            try
            {
                var selectedItems = _accountsRail.SelectedItems;
                if (selectedItems is not null)
                {
                    selectedItems.Clear();
                    foreach (var account in _viewModel.Accounts) selectedItems.Add(account);
                }
            }
            finally { _suppressAccountSelection = false; }
            await SaveAsync();
            RefreshAccountRail();
            RenderPage();
        };
        selectionActions.Children.Add(selectAll);
        var selectNone = new Button { Content = "Select none", Padding = new Thickness(10, 6) };
        StyleButton(selectNone, secondary: true);
        selectNone.Click += async (_, _) =>
        {
            _queueSelectedAccounts.Clear();
            _viewModel.SelectedAccount = null;
            Interlocked.Increment(ref _browserActivationVersion);
            _suppressAccountSelection = true;
            try { _accountsRail.SelectedItems?.Clear(); }
            finally { _suppressAccountSelection = false; }
            await SaveAsync();
            RefreshAccountRail();
            RenderPage();
        };
        Grid.SetColumn(selectNone, 1);
        selectionActions.Children.Add(selectNone);
        accountActions.Children.Add(selectionActions);

        var transferActions = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 82, ItemHeight = 32 };
        var export = new Button { Content = "Export", Padding = new Thickness(10, 6) };
        StyleButton(export, secondary: true);
        export.Click += async (_, _) =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-account-manager-profile-export.json");
            await ProfileTransferService.ExportAsync(path, _viewModel.Accounts, _viewModel.Presets, _viewModel.Settings);
            _viewModel.AppendActivity($"Exported profiles, presets, and settings to {path}. Browser cookies were not included.");
        };
        transferActions.Children.Add(export);
        var import = new Button { Content = "Import", Padding = new Thickness(10, 6) };
        StyleButton(import, secondary: true);
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
                foreach (var preset in package.Presets.Where(item => _viewModel.Presets.All(existing => !string.Equals(existing.Name, item.Name, StringComparison.OrdinalIgnoreCase)))) _viewModel.Presets.Add(preset);
                _viewModel.ImportSettings(package.Settings);
                await SaveAsync();
                _viewModel.AppendActivity($"Imported {package.Accounts.Count} profile(s) and {package.Presets.Count} preset(s). Sign in again in each new browser session.");
                RenderPage();
            }
            catch (Exception exception) { _viewModel.AppendActivity($"Profile import rejected: {exception.Message}"); }
        };
        transferActions.Children.Add(import);
        accountActions.Children.Add(transferActions);
        accountActions.Children.Add(new TextBlock { Text = "Sessions stay isolated and local to this PC.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = MutedTextBrush, Margin = new Thickness(3, 5, 3, 0) });
        Grid.SetRow(accountActions, 3);
        shell.Children.Add(accountActions);

        return new Border
        {
            Background = SurfaceBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(17),
            Child = shell
        };
    }

    private Control BuildAccountRailRow(AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var accountId = account.Id;
        var accountLabel = account.Label ?? "Roblox account";
        var accountGroup = account.Group ?? string.Empty;
        var open = new CheckBox
        {
            Content = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = accountLabel, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = string.IsNullOrWhiteSpace(accountGroup) ? "" : accountGroup, FontSize = 10, Foreground = MutedTextBrush, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            },
            IsChecked = _queueSelectedAccounts.Contains(accountId),
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        _accountChecks[accountId] = open;
        open.Click += async (_, _) =>
        {
            if (open.IsChecked == true) _queueSelectedAccounts.Add(account.Id);
            else _queueSelectedAccounts.Remove(account.Id);
            await SaveAsync();
            _suppressAccountSelection = true;
            try
            {
                var selectedItems = _accountsRail.SelectedItems;
                if (selectedItems is not null)
                {
                    if (open.IsChecked == true && !selectedItems.Contains(account)) selectedItems.Add(account);
                    if (open.IsChecked != true && selectedItems.Contains(account)) selectedItems.Remove(account);
                }
            }
            finally { _suppressAccountSelection = false; }
            var remaining = _accountsRail.SelectedItems?.OfType<AccountProfile>().LastOrDefault();
            var wasActive = _viewModel.SelectedAccount?.Id == account.Id;
            if (open.IsChecked == true)
            {
                _viewModel.SelectedAccount = account;
                UpdateAccountSelectionVisuals();
                SelectPage("accounts");
                await OpenAccountAsync(account);
            }
            else if (wasActive)
            {
                _viewModel.SelectedAccount = remaining;
                if (remaining is null)
                {
                    Interlocked.Increment(ref _browserActivationVersion);
                    UpdateAccountSelectionVisuals();
                    SelectPage("accounts");
                }
                else
                {
                    UpdateAccountSelectionVisuals();
                    SelectPage("accounts");
                    await OpenAccountAsync(remaining);
                }
            }
            else
            {
                UpdateAccountSelectionVisuals();
                SelectPage("accounts");
            }
        };
        var favorite = new Button { Content = account.IsFavorite ? "★" : "☆", Padding = new Thickness(6, 2), FontSize = 18, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = account.IsFavorite ? new SolidColorBrush(Avalonia.Media.Color.Parse("#F5C451")) : MutedTextBrush };
        favorite.Click += async (_, _) =>
        {
            account.IsFavorite = !account.IsFavorite;
            favorite.Content = account.IsFavorite ? "★" : "☆";
            favorite.Foreground = account.IsFavorite ? new SolidColorBrush(Avalonia.Media.Color.Parse("#F5C451")) : MutedTextBrush;
            await SaveAsync();
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetColumn(favorite, 1);
        row.Children.Add(open);
        row.Children.Add(favorite);
        var card = new Border { Background = _queueSelectedAccounts.Contains(account.Id) ? SelectionSurfaceBrush : Brushes.Transparent, BorderBrush = _queueSelectedAccounts.Contains(account.Id) ? SelectionBorderBrush : Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = row };
        _accountCards[account.Id] = card;
        return card;
    }

    private void UpdateAccountSelectionVisuals()
    {
        foreach (var account in _viewModel.Accounts)
        {
            if (!_accountCards.TryGetValue(account.Id, out var card)) continue;
            var selected = _queueSelectedAccounts.Contains(account.Id);
            if (_accountChecks.TryGetValue(account.Id, out var check)) check.IsChecked = selected;
            card.Background = selected ? SelectionSurfaceBrush : Brushes.Transparent;
            card.BorderBrush = selected ? SelectionBorderBrush : Brushes.Transparent;
        }
    }

    private Border BuildActivityCard()
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 9 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = "Activity", FontSize = 13, FontWeight = FontWeight.SemiBold });
        title.Children.Add(new TextBlock { Text = "Live diagnostics", FontSize = 11, Foreground = MutedTextBrush, VerticalAlignment = VerticalAlignment.Center });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title);

        _activityTimeout.ItemsSource = new[] { "30s", "45s", "60s", "90s" };
        _activityTimeout.SelectedIndex = 1;
        _activityTimeout.Width = 82;
        _activityTimeout.Padding = new Thickness(10, 6);
        _activityTimeout.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Add(new TextBlock { Text = "Timeout", Foreground = MutedTextBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_activityTimeout);

        _continueOnFailure.Content = "Continue on failure";
        _continueOnFailure.IsChecked = _viewModel.Settings.ContinueOnFailure;
        _continueOnFailure.Foreground = TextBrush;
        _continueOnFailure.VerticalAlignment = VerticalAlignment.Center;
        _continueOnFailure.Click += async (_, _) =>
        {
            _viewModel.Settings.ContinueOnFailure = _continueOnFailure.IsChecked == true;
            await SaveAsync();
        };
        controls.Children.Add(_continueOnFailure);

        _cancelLaunch.Content = "Cancel";
        StyleButton(_cancelLaunch, secondary: true);
        _cancelLaunch.Padding = new Thickness(12, 6);
        _cancelLaunch.IsEnabled = false;
        _cancelLaunch.Click += (_, _) => _launchCancellation?.Cancel();
        controls.Children.Add(_cancelLaunch);

        _retryFailed.Content = "Retry failed";
        StyleButton(_retryFailed, secondary: true);
        _retryFailed.Padding = new Thickness(12, 6);
        _retryFailed.IsEnabled = false;
        _retryFailed.Click += async (_, _) => await RetryFailedLaunchesAsync();
        controls.Children.Add(_retryFailed);

        _copyActivity.Content = "Copy log";
        StyleButton(_copyActivity, secondary: true);
        _copyActivity.Padding = new Thickness(12, 6);
        _copyActivity.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(_activity.Text ?? string.Empty);
            _viewModel.AppendActivity("Activity log copied to the clipboard.");
        };
        controls.Children.Add(_copyActivity);
        Grid.SetColumn(controls, 1);
        header.Children.Add(controls);
        layout.Children.Add(header);

        _queueSummary.Margin = new Thickness(0, 0, 0, 0);
        _queueSummary.Children.Clear();
        var queueScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _queueSummary
        };
        Grid.SetRow(queueScroll, 1);
        layout.Children.Add(queueScroll);

        Grid.SetRow(_activity, 2);
        layout.Children.Add(_activity);
        return new Border
        {
            Background = SurfaceBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Padding = new Thickness(13),
            Child = layout
        };
    }

    private void RefreshQueueSummary()
    {
        _queueSummary.Children.Clear();
        foreach (var item in _viewModel.Queue)
        {
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 3, 0, 0) };
            status.Children.Add(new TextBlock { Text = item.State.ToString().ToUpperInvariant(), FontSize = 9, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#B5A7FF")) });
            status.Children.Add(new TextBlock { Text = "•", FontSize = 9, Foreground = MutedTextBrush });
            status.Children.Add(new TextBlock { Text = item.Detail, FontSize = 10, Foreground = MutedTextBrush, TextTrimming = TextTrimming.CharacterEllipsis, Width = 84 });
            _queueSummary.Children.Add(new Border
            {
                Background = InputBrush,
                BorderBrush = ControlBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7),
                Width = 160,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = item.Label, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        status
                    }
                }
            });
        }
    }

    private void UpdateActivityControls()
    {
        _continueOnFailure.IsChecked = _viewModel.Settings.ContinueOnFailure;
        _cancelLaunch.IsEnabled = _launchCancellation is not null;
        _retryFailed.IsEnabled = _launchCancellation is null && _viewModel.Queue.Any(item => item.State == LaunchQueueState.Failed);
        if (_launchSelected is not null) _launchSelected.IsEnabled = _launches is not null && _launchCancellation is null;
        if (_queueLaunch is not null) _queueLaunch.IsEnabled = _launches is not null && _launchCancellation is null;
        _activityTimeout.SelectedItem = $"{Math.Clamp(_viewModel.Settings.LaunchTimeoutSeconds, 30, 90)}s";
        RefreshQueueSummary();
    }

    private static void StyleButton(Button button, bool secondary = false, bool danger = false)
    {
        button.FontSize = 13;
        button.FontWeight = FontWeight.SemiBold;
        button.Foreground = danger ? DangerTextBrush : TextBrush;
        button.Background = danger ? DangerBrush : secondary ? HoverBrush : AccentBrush;
        button.BorderBrush = danger ? new SolidColorBrush(Avalonia.Media.Color.Parse("#4A2732")) : secondary ? ControlBorderBrush : AccentBrush;
        button.BorderThickness = new Thickness(1);
        button.Padding = new Thickness(15, 9);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.PointerEntered += (_, _) =>
        {
            if (button.IsEnabled) button.Background = danger ? new SolidColorBrush(Avalonia.Media.Color.Parse("#3A2029")) : secondary ? new SolidColorBrush(Avalonia.Media.Color.Parse("#282E3D")) : AccentHoverBrush;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = danger ? DangerBrush : secondary ? HoverBrush : AccentBrush;
        };
    }

    private void SelectPage(string pageKey) => _ = SelectPageAsync(pageKey);

    private async Task SelectPageAsync(string pageKey)
    {
        await _pageNavigationGate.WaitAsync();
        try
        {
            var page = _viewModel.Pages.FirstOrDefault(candidate => string.Equals(candidate.Key, pageKey, StringComparison.Ordinal));
            if (page is null) return;
            if (_clientViewVisible && !string.Equals(pageKey, "clients", StringComparison.Ordinal)
                && !await DeactivateClientOverlayAsync())
            {
                SetClientOverlayStatus(
                    "Could not restore every Roblox window. Grant Accessibility permission, then try navigating again.",
                    failure: true);
                return;
            }
            _viewModel.SelectedPage = page;
            UpdatePresetRevealButton();
            RenderPage();
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Navigation paused: {LaunchDiagnostics.SanitiseCode(exception.Message)}");
        }
        finally { _pageNavigationGate.Release(); }
    }

    private void RefreshAccountRail()
    {
        var selected = (_accountsRail.SelectedItems ?? Array.Empty<object>()).OfType<AccountProfile>().ToArray();
        _accountsRail.ItemsSource = null;
        _accountsRail.ItemsSource = _viewModel.Accounts;
        _suppressAccountSelection = true;
        try
        {
            var selectedItems = _accountsRail.SelectedItems;
            if (selectedItems is not null)
            {
                foreach (var account in selected) selectedItems.Add(account);
            }
        }
        finally { _suppressAccountSelection = false; }
        UpdateAccountSelectionVisuals();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _viewModel.LoadAsync();
            if (OperatingSystem.IsMacOS())
            {
                await LogMacRobloxClientVersionAsync();
            }
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DesktopShellViewModel.Activity)) _activity.Text = _viewModel.Activity;
            };
            _activity.Text = _viewModel.Activity;
            var startupPlan = DesktopStartupPlan.Create(_viewModel.Accounts, _validationMode);
            var restoredAccounts = DesktopStartupPlan.RestoreSelectedAccounts(_viewModel.Accounts, _viewModel.Settings);
            RenderPage();
            if (restoredAccounts.Count > 0)
            {
                _viewModel.SelectedAccount = restoredAccounts[0];
                foreach (var account in restoredAccounts) _queueSelectedAccounts.Add(account.Id);
                if (_validationMode != DesktopValidationMode.BrowserStartup)
                {
                    _suppressAccountSelection = true;
                    try
                    {
                        if (_accountsRail.SelectedItems is not null)
                        {
                            foreach (var account in restoredAccounts) _accountsRail.SelectedItems.Add(account);
                        }
                    }
                    finally { _suppressAccountSelection = false; }
                }
                UpdateAccountSelectionVisuals();
                RenderPage();
            }
            if (startupPlan.ActivateBrowserOnStartup && restoredAccounts.Count > 0)
            {
                await Task.Delay(250);
                await ValidateBrowserStartupAsync(restoredAccounts[0]);
            }
            else if (_validationMode == DesktopValidationMode.GuiStartup)
            {
                RequestValidationShutdown();
            }
            else
            {
                _ = CheckForUpdateAtStartupAsync();
            }
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Startup error: {exception.Message}");
            if (_validationMode == DesktopValidationMode.None)
            {
                RenderPage();
            }
            else
            {
                RequestValidationShutdown(1);
            }
        }
    }

    private void RenderPage()
    {
        DetachBrowserHost();
        UpdatePresetRevealButton();
        var isWorkspace = _viewModel.SelectedPage.Key is "accounts" or "browser";
        _pageTitle.Text = isWorkspace ? "Launch workspace" : _viewModel.SelectedPage.Title;
        _pageDescription.Text = isWorkspace ? "Choose profiles, pick a game, and launch every client in sequence." : _viewModel.PageStatus;
        _content.Content = _viewModel.SelectedPage.Key switch
        {
            "accounts" or "browser" => BuildLaunchWorkspacePage(),
            "presets" => BuildPresetsPage(),
            "queue" => BuildQueuePage(),
            "clients" => BuildClientsPage(),
            "activity" => BuildActivityPage(),
            "settings" => BuildSettingsPage(),
            "plugins" => BuildPluginsPage(),
            "diagnostics" => BuildDiagnosticsPage(),
            _ => new TextBlock { Text = "Select a page." }
        };
        UpdateActivityControls();
        Interlocked.Increment(ref _browserActivationVersion);
    }

    private void DetachBrowserHost()
    {
        switch (_browserHost.Parent)
        {
            case Panel panel:
                panel.Children.Remove(_browserHost);
                break;
            case ContentControl content when ReferenceEquals(content.Content, _browserHost):
                content.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, _browserHost):
                decorator.Child = null;
                break;
        }
    }

    private Control BuildLaunchWorkspacePage()
    {
        var selectedPreset = _viewModel.SelectedPreset ?? _viewModel.Presets.FirstOrDefault();
        var presetPicker = new ComboBox
        {
            ItemsSource = _viewModel.Presets,
            SelectedItem = selectedPreset,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 260,
            Background = InputBrush,
            Foreground = TextBrush,
            BorderBrush = ControlBorderBrush,
            Padding = new Thickness(11, 8)
        };
        presetPicker.SelectionChanged += (_, _) => _viewModel.SelectedPreset = presetPicker.SelectedItem as GamePreset;

        var presetSearch = new TextBox { PlaceholderText = "Search game presets" };
        presetSearch.TextChanged += (_, _) =>
        {
            var query = presetSearch.Text?.Trim() ?? string.Empty;
            var selected = presetPicker.SelectedItem as GamePreset;
            var filteredPresets = DesktopPresetPolicy.FilterPresets(_viewModel.Presets, query);
            presetPicker.ItemsSource = query.Length == 0 ? _viewModel.Presets : filteredPresets;
            if (selected is null || !filteredPresets.Contains(selected))
            {
                presetPicker.SelectedItem = filteredPresets.FirstOrDefault();
            }
        };
        var presetActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 5 };
        presetActions.Children.Add(presetPicker);
        var presetActionNames = new[]
        {
            (Label: "Add preset", Key: "add"),
            (Label: "Remove preset", Key: "remove"),
            (Label: "Edit preset", Key: "edit"),
            (Label: "Duplicate preset", Key: "duplicate"),
            (Label: "Import presets", Key: "import"),
            (Label: "Export presets", Key: "export")
        };
        for (var i = 0; i < presetActionNames.Length; i++)
        {
            var action = new Button
            {
                Content = BuildPresetActionIcon(presetActionNames[i].Key),
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                IsEnabled = presetActionNames[i].Key is "add" or "import" or "export"
                    || presetActionNames[i].Key is "duplicate" && selectedPreset?.Url is not null && selectedPreset.Url.Length > 0
                    || presetActionNames[i].Key is "remove" or "edit" && selectedPreset is { IsBuiltIn: false }
            };
            ToolTip.SetTip(action, presetActionNames[i].Label);
            StyleButton(action, secondary: true);
            var actionKey = presetActionNames[i].Key;
            action.Click += async (_, _) => await HandlePresetActionAsync(actionKey);
            Grid.SetColumn(action, i + 1);
            presetActions.Children.Add(action);
        }

        var presetControls = new StackPanel { Spacing = 5 };
        presetControls.Children.Add(new TextBlock { Text = "GAME PRESET", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = MutedTextBrush });
        presetControls.Children.Add(presetSearch);
        presetControls.Children.Add(presetActions);

        var customUrl = new TextBox
        {
            Text = DesktopPresetPolicy.GetUrlEditorValue(selectedPreset),
            Foreground = MutedTextBrush,
            IsReadOnly = !DesktopPresetPolicy.IsCustomUrlPreset(selectedPreset),
            PlaceholderText = "https://www.roblox.com/games/123/example",
            MaxWidth = 260,
            Background = InputBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 10)
        };
        presetPicker.SelectionChanged += (_, _) =>
        {
            var preset = presetPicker.SelectedItem as GamePreset;
            _viewModel.SelectedPreset = preset;
            customUrl.Text = DesktopPresetPolicy.GetUrlEditorValue(preset);
            customUrl.IsReadOnly = !DesktopPresetPolicy.IsCustomUrlPreset(preset);
        };
        var customUrlPanel = new StackPanel { Spacing = 5, Margin = new Thickness(12, 0, 0, 0) };
        customUrlPanel.Children.Add(new TextBlock { Text = "CUSTOM ROBLOX URL", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = MutedTextBrush });
        customUrlPanel.Children.Add(customUrl);

        var login = new Button { Content = "Login / Home", Margin = new Thickness(12, 16, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        StyleButton(login, secondary: true);
        login.Click += async (_, _) => await ShowLoginAsync();
        var accounts = _viewModel.Accounts.Where(account => _queueSelectedAccounts.Contains(account.Id)).ToList();
        var launch = new Button { Content = "▶  Auto-launch selected", IsEnabled = _launches is not null && _launchCancellation is null, Margin = new Thickness(0, 16, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        _launchSelected = launch;
        StyleButton(launch);
        launch.Click += async (_, _) =>
        {
            _viewModel.Settings.LaunchTimeoutSeconds = GetSelectedTimeoutSeconds();
            _viewModel.Settings.ContinueOnFailure = _continueOnFailure.IsChecked == true;
            await SaveAsync();
            await RunLaunchQueueAsync(presetPicker.SelectedItem as GamePreset, accounts, customUrl.Text);
        };

        var presetBar = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(15),
            Margin = new Thickness(0, 0, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("500,*,Auto,Auto"),
                ColumnSpacing = 0,
                Children = { presetControls, customUrlPanel, login, launch }
            }
        };
        Grid.SetColumn(customUrlPanel, 1);
        Grid.SetColumn(login, 2);
        Grid.SetColumn(launch, 3);

        var sessionHeader = BuildSessionNavigationBar();

        _browserHost.MinHeight = 160;
        _browserHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _browserHost.VerticalAlignment = VerticalAlignment.Stretch;
        _browserHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _browserHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        _browserHost.Margin = new Thickness(0);
        if (_viewModel.SelectedAccount is null)
        {
            _browserHost.Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = "Select an account profile", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Its private Roblox session will open here.", Foreground = MutedTextBrush, HorizontalAlignment = HorizontalAlignment.Center }
                }
            };
        }
        else if (_browserSessions.HasSession(_viewModel.SelectedAccount.Id))
        {
            _browserHost.Content = _browserSessions.GetView(_viewModel.SelectedAccount.Id);
        }
        else
        {
            _browserHost.Content = new TextBlock { Text = "Click Browse or Login / Home to open the isolated Roblox session.", Foreground = MutedTextBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        var browserBody = new Grid { RowDefinitions = new RowDefinitions("46,3,*"), Background = InputBrush, ClipToBounds = true };
        browserBody.Children.Add(sessionHeader);
        var progress = new Border { Background = AccentBrush, Height = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(progress, 1);
        browserBody.Children.Add(progress);
        Grid.SetRow(_browserHost, 2);
        browserBody.Children.Add(_browserHost);
        var browserCard = new Border { Background = SurfaceBrush, BorderBrush = ControlBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = browserBody };

        var showPresetPanel = _viewModel.Settings.ShowGamePresetPanel;
        var workspace = new Grid
        {
            RowDefinitions = new RowDefinitions(showPresetPanel ? "Auto,*,Auto" : "*,Auto"),
            RowSpacing = 14,
            ClipToBounds = true
        };
        if (showPresetPanel) workspace.Children.Add(presetBar);
        Grid.SetRow(browserCard, 1);
        if (!showPresetPanel) Grid.SetRow(browserCard, 0);
        workspace.Children.Add(browserCard);
        var hint = new TextBlock { Text = "Sessions stay isolated and local to this PC. Browser data is never included in exports.", FontSize = 11, Foreground = MutedTextBrush, Margin = new Thickness(3, 0, 3, 0) };
        Grid.SetRow(hint, showPresetPanel ? 2 : 1);
        workspace.Children.Add(hint);
        return workspace;
    }

    private static Control BuildPresetActionIcon(string action)
    {
        var data = action switch
        {
            "add" => "M10 4H14V10H20V14H14V20H10V14H4V10H10Z",
            "remove" => "M4 10H20V14H4Z",
            "edit" => "M5 17.5V20H7.5L19 8.5L15.5 5L5 15.5Z M14 6.5L17.5 10",
            "duplicate" => "M7 7H18V18H7Z M4 4H15V7H7V15H4Z",
            "import" => "M10 4H14V12H18L12 18L6 12H10Z M4 19H20V22H4Z",
            "export" => "M10 15H14V7H18L12 1L6 7H10Z M4 19H20V22H4Z",
            _ => "M4 4H20V20H4Z"
        };
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Fill = TextBrush,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Grid BuildSessionNavigationBar()
    {
        var sessionHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = ElevatedBrush, MinHeight = 46 };
        var sessionName = _viewModel.SelectedAccount?.Label ?? "No profile selected";
        var sessionTitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0) };
        sessionTitle.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = AccentBrush });
        sessionTitle.Children.Add(new TextBlock { Text = sessionName, FontSize = 13, FontWeight = FontWeight.SemiBold });
        sessionTitle.Children.Add(new TextBlock { Text = "•  SESSION", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = MutedTextBrush, VerticalAlignment = VerticalAlignment.Center });
        sessionHeader.Children.Add(sessionTitle);

        var sessionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var browse = new Button { Content = "Browse", Padding = new Thickness(14, 5) };
        StyleButton(browse, secondary: true);
        browse.Click += async (_, _) =>
        {
            SelectPage("browser");
            if (_viewModel.SelectedAccount is not null)
                await OpenAccountAsync(_viewModel.SelectedAccount);
        };
        var clients = new Button { Content = "Clients", Padding = new Thickness(14, 5) };
        StyleButton(clients, secondary: true);
        clients.Click += (_, _) => SelectPage("clients");
        sessionActions.Children.Add(browse);
        sessionActions.Children.Add(clients);
        Grid.SetColumn(sessionActions, 1);
        sessionHeader.Children.Add(sessionActions);
        return sessionHeader;
    }

    private async Task HandlePresetActionAsync(string action)
    {
        switch (action)
        {
            case "add":
            {
                var values = await PromptPresetAsync("New game preset", "New preset", "https://www.roblox.com/games/");
                if (values is null || !GamePreset.TryNormalizeRobloxGameUrl(values.Value.Url, out var normalized)) return;
                var preset = new GamePreset(values.Value.Name, normalized);
                _viewModel.Presets.Add(preset);
                _viewModel.SelectedPreset = preset;
                await SaveAsync();
                RenderPage();
                break;
            }
            case "remove" when _viewModel.SelectedPreset is { IsBuiltIn: false } remove:
                if (!await ConfirmAsync($"Remove the preset '{remove.Name}'?")) return;
                _viewModel.Presets.Remove(remove);
                _viewModel.SelectedPreset = _viewModel.Presets.FirstOrDefault();
                await SaveAsync();
                RenderPage();
                break;
            case "edit" when _viewModel.SelectedPreset is { IsBuiltIn: false } edit:
            {
                var values = await PromptPresetAsync("Edit game preset", edit.Name, edit.Url);
                if (values is null || !GamePreset.TryNormalizeRobloxGameUrl(values.Value.Url, out var normalized)) return;
                var index = _viewModel.Presets.IndexOf(edit);
                if (index < 0) return;
                var updated = edit with { Name = values.Value.Name, Url = normalized };
                _viewModel.Presets[index] = updated;
                _viewModel.SelectedPreset = updated;
                await SaveAsync();
                RenderPage();
                break;
            }
            case "duplicate" when _viewModel.SelectedPreset is { Url.Length: > 0 } duplicate:
            {
                var name = $"{duplicate.Name} copy";
                var suffix = 2;
                while (_viewModel.Presets.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{duplicate.Name} copy {suffix++}";
                var copy = new GamePreset(name, duplicate.Url);
                _viewModel.Presets.Add(copy);
                _viewModel.SelectedPreset = copy;
                await SaveAsync();
                RenderPage();
                break;
            }
            case "import":
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-presets.json");
                if (!File.Exists(path)) { _viewModel.AppendActivity($"Preset import file not found: {path}"); return; }
                try
                {
                    foreach (var preset in await PresetTransferService.ImportAsync(path))
                        if (_viewModel.Presets.All(existing => !string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase))) _viewModel.Presets.Add(preset);
                    await SaveAsync();
                    RenderPage();
                }
                catch (Exception exception) { _viewModel.AppendActivity($"Preset import rejected: {exception.Message}"); }
                break;
            }
            case "export":
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "roblox-presets.json");
                await PresetTransferService.ExportAsync(path, _viewModel.Presets);
                _viewModel.AppendActivity($"Exported presets to {path}.");
                break;
            }
        }
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
        var launch = new Button { Content = _launches is null ? "Launch unavailable until Roblox trust is configured" : "Launch selected accounts", IsEnabled = _launches is not null && _launchCancellation is null };
        _queueLaunch = launch;
        launch.Click += async (_, _) => await RunLaunchQueueAsync(picker.SelectedItem as GamePreset, selected);
        var cancel = new Button { Content = "Cancel", IsEnabled = _launchCancellation is not null };
        cancel.Click += (_, _) => _launchCancellation?.Cancel();
        var queueList = new ListBox { ItemsSource = _viewModel.Queue, Height = 230 };
        var info = new TextBlock { Text = selected.Count == 0 ? "Favorite an account or open one from Accounts to include it in the queue." : $"{selected.Count} account(s) selected." , TextWrapping = TextWrapping.Wrap };
        return Card(new StackPanel { Spacing = 10, Children = { info, new TextBlock { Text = "Game preset", FontWeight = FontWeight.SemiBold }, picker, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { launch, cancel } }, queueList } });
    }

    private Control BuildClientsPage()
    {
        if (_clientOverlay is not null) return BuildClientOverlayPage();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(BuildSessionNavigationBar());
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
        while (panel.Children.Count > 3) panel.Children.RemoveAt(3);
        var windows = await _clients.GetWindowsAsync();
        foreach (var window in windows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = $"PID {window.Process.Pid} · {Path.GetFileName(window.Process.ExecutablePath)}", Width = 320, VerticalAlignment = VerticalAlignment.Center });
            var focus = new Button { Content = "Focus" };
            focus.Click += async (_, _) => _viewModel.AppendActivity(await _clients.FocusAsync(window) ? "Focused client." : "Focus denied; check Accessibility permission.");
            var close = new Button { Content = "Close" };
            close.Click += async (_, _) =>
            {
                try
                {
                    await _clients.CloseAsync(new CoreRobloxProcessInfo(window.Process, true));
                    _viewModel.AppendActivity("Requested a verified client close.");
                }
                catch (Exception exception) { _viewModel.AppendActivity($"Client close rejected: {exception.Message}"); }
            };
            row.Children.Add(focus); row.Children.Add(close); panel.Children.Add(row);
        }
        if (windows.Count == 0) panel.Children.Add(new TextBlock { Text = "No RAM-managed Roblox clients are running.", Opacity = 0.65 });
    }

    private Control BuildClientOverlayPage()
    {
        _clientOverlayActivation?.Cancel();
        _clientOverlayActivation?.Dispose();
        _clientOverlayActivation = new CancellationTokenSource();
        Interlocked.Increment(ref _clientOverlayGeneration);
        _clientViewVisible = true;
        _clientOverlayTimer?.Start();

        _clientTabsControl = new ListBox
        {
            ItemsSource = _clientTabs,
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<ClientTabItem>((item, _) => item is null ? null : BuildClientTab(item))
        };
        _clientTabsControl.SelectionChanged += async (_, _) =>
        {
            if (_suppressClientSelection || _clientTabsControl.SelectedItem is not ClientTabItem selected) return;
            _selectedClientAccountId = selected.AccountId;
            await RefreshClientOverlayAsync(explicitUserSelection: true);
        };

        _clientViewport = new Border
        {
            Background = InputBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            MinHeight = 320,
            Child = new TextBlock
            {
                Text = "Select a running client tab",
                Foreground = MutedTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _clientViewport.SizeChanged += async (_, _) => await RefreshClientOverlayAsync();
        _clientViewport.AttachedToVisualTree += async (_, _) => await RefreshClientOverlayAsync();

        _clientOverlayStatus = new TextBlock
        {
            Text = "Looking for opted-in Roblox clients…",
            Foreground = MutedTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _clientOverlayStatus }
        };
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8,
            Children = { _clientTabsControl, _clientViewport, controls }
        };
        Grid.SetRow(_clientViewport, 1);
        Grid.SetRow(controls, 2);
        _ = RefreshClientOverlayAsync();
        return Card(layout);
    }

    private Control BuildClientTab(ClientTabItem item)
    {
        var label = new TextBlock
        {
            Text = item.Label,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tab = new Border
        {
            Background = ElevatedBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 6, 0),
            Child = label
        };
        tab.PointerPressed += async (_, _) =>
        {
            _selectedClientAccountId = item.AccountId;
            await RefreshClientOverlayAsync(explicitUserSelection: true);
        };
        return tab;
    }

    private async Task RefreshClientOverlayAsync(bool explicitUserSelection = false)
    {
        if (!_clientViewVisible || _clientOverlay is null || _clients is null || _clientRefreshInProgress) return;
        var activation = _clientOverlayActivation;
        if (activation is null) return;
        var cancellationToken = activation.Token;
        var generation = Interlocked.Read(ref _clientOverlayGeneration);
        _clientRefreshInProgress = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = await _clients.GetWindowsAsync(cancellationToken);
            EnsureClientOverlayActive(generation, cancellationToken);
            var accountsById = _viewModel.Accounts.ToDictionary(account => account.Id, StringComparer.Ordinal);
            var eligible = windows
                .Where(window => !string.IsNullOrWhiteSpace(window.AccountId)
                    && accountsById.TryGetValue(window.AccountId, out var account)
                    && account.EmbedInClients)
                .ToArray();

            var nextIds = eligible.Select(window => window.AccountId!).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _clientTabs.Select(tab => tab.AccountId).Where(id => !nextIds.Contains(id)).ToArray())
            {
                await _clientOverlay.RestoreAsync(removed, cancellationToken);
                EnsureClientOverlayActive(generation, cancellationToken);
            }

            _suppressClientSelection = true;
            try
            {
                _clientTabs.Clear();
                foreach (var window in eligible)
                {
                    var account = accountsById[window.AccountId!];
                    _clientTabs.Add(new ClientTabItem(account.Id, account.Label, window));
                }
                if (_selectedClientAccountId is null || !nextIds.Contains(_selectedClientAccountId))
                    _selectedClientAccountId = _clientTabs.FirstOrDefault()?.AccountId;
                if (_clientTabsControl is not null)
                    _clientTabsControl.SelectedItem = _clientTabs.FirstOrDefault(tab => tab.AccountId == _selectedClientAccountId);
            }
            finally { _suppressClientSelection = false; }

            if (eligible.Length == 0 || _selectedClientAccountId is null)
            {
                var restore = await _clientOverlay.RestoreAllAsync(cancellationToken);
                EnsureClientOverlayActive(generation, cancellationToken);
                SetClientOverlayStatus(
                    restore.Succeeded
                        ? "No opted-in RAM-managed Roblox clients are running."
                        : "No opted-in clients are running, but a prior window restoration will be retried.",
                    failure: !restore.Succeeded);
                return;
            }
            if (!TryGetClientViewport(out var viewport, out var viewportFailure))
            {
                var restore = await _clientOverlay.RestoreAllAsync(cancellationToken);
                EnsureClientOverlayActive(generation, cancellationToken);
                SetClientOverlayStatus(
                    restore.Succeeded
                        ? viewportFailure
                        : $"{viewportFailure} A prior window restoration will be retried.",
                    failure: !restore.Succeeded);
                return;
            }

            EnsureClientOverlayActive(generation, cancellationToken);
            var result = await _clientOverlay.ShowOnlyAsync(
                eligible,
                _selectedClientAccountId,
                viewport,
                explicitUserSelection,
                canRaise: () => !cancellationToken.IsCancellationRequested
                    && _launcherIsActive
                    && Interlocked.Read(ref _clientOverlayGeneration) == generation,
                cancellationToken: cancellationToken);
            EnsureClientOverlayActive(generation, cancellationToken);
            SetClientOverlayStatus(
                result.Succeeded
                    ? explicitUserSelection
                        ? "Roblox remains an external top-level window over this viewport. Input is routed directly by macOS."
                        : "Client placement is ready. Select a tab to bring Roblox in front of RAM."
                    : DescribeOverlayFailure(result.DiagnosticCode),
                failure: !result.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Page teardown owns restoration after invalidating this refresh generation.
        }
        catch (Exception exception)
        {
            SetClientOverlayStatus($"Client overlay paused: {LaunchDiagnostics.SanitiseCode(exception.Message)}", failure: true);
            try { await _clientOverlay.RestoreAllAsync(); } catch { }
        }
        finally { _clientRefreshInProgress = false; }
    }

    private bool TryGetClientViewport(out MacWindowFrame viewport, out string failure)
    {
        viewport = default;
        failure = "Waiting for the Clients viewport layout…";
        var clientViewport = _clientViewport;
        if (clientViewport is null
            || clientViewport.Bounds.Width < 400 || clientViewport.Bounds.Height < 300) return false;
        var topLevel = TopLevel.GetTopLevel(clientViewport);
        if (topLevel is null) return false;
        var point = clientViewport.PointToScreen(new Point(0, 0));
        var screens = topLevel.Screens;
        if (screens is null) return false;
        var screen = screens.ScreenFromPoint(point);
        if (screen is null) return false;
        if (screens.All.Any(candidate => Math.Abs(candidate.Scaling - screen.Scaling) > 0.001))
        {
            failure = "Client overlay is paused while displays use different scaling. Move RAM to a single-scale display setup.";
            return false;
        }
        viewport = MacViewportCoordinateConverter.FromAvaloniaPixels(
            point.X,
            point.Y,
            clientViewport.Bounds.Width,
            clientViewport.Bounds.Height,
            screen.Scaling);
        return viewport.IsValid;
    }

    private async Task<bool> DeactivateClientOverlayAsync()
    {
        InvalidateClientOverlay();
        if (_clientOverlay is null) return true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await _clientOverlay.RestoreAllAsync();
                if (result.Succeeded) return true;
            }
            catch
            {
            }
            if (attempt < 2) await Task.Delay(100);
        }
        _clientViewVisible = true;
        return false;
    }

    private void InvalidateClientOverlay()
    {
        _clientViewVisible = false;
        _clientOverlayTimer?.Stop();
        Interlocked.Increment(ref _clientOverlayGeneration);
        _clientOverlayActivation?.Cancel();
    }

    private void EnsureClientOverlayActive(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_clientViewVisible || Interlocked.Read(ref _clientOverlayGeneration) != generation)
            throw new OperationCanceledException(cancellationToken);
    }

    private void SetClientOverlayStatus(string message, bool failure)
    {
        if (_clientOverlayStatus is null) return;
        _clientOverlayStatus.Text = message;
        _clientOverlayStatus.Foreground = failure ? DangerTextBrush : MutedTextBrush;
    }

    private static string DescribeOverlayFailure(string code) => code switch
    {
        "accessibility-permission-required" => "Grant Accessibility permission to place Roblox over the Clients viewport.",
        "accessible-window-not-ready" or "accessible-window-changed" => "Waiting for a stable Roblox game window…",
        "fullscreen-window-not-supported" => "Exit Roblox fullscreen mode before using the Clients panel.",
        "stale-process-identity" => "A Roblox process identity changed; refresh or relaunch that account.",
        "raise-cancelled" => "Client placement is ready. Select its tab again to bring Roblox forward.",
        _ => $"Client overlay paused: {LaunchDiagnostics.SanitiseCode(code)}"
    };

    private Control BuildActivityPage()
    {
        var log = new TextBlock { Text = _viewModel.Activity, TextWrapping = TextWrapping.NoWrap };
        var scroll = new ScrollViewer
        {
            Content = log,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        layout.Children.Add(new TextBlock { Text = "Sanitized activity", FontWeight = FontWeight.SemiBold });
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        return Card(layout);
    }

    private Control BuildSettingsPage()
    {
        var tabs = new TabControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        tabs.Items.Add(new TabItem { Header = "General", Content = ScrollSettings(BuildGeneralSettingsTab()) });
        tabs.Items.Add(new TabItem { Header = "Global Defaults", Content = ScrollSettings(BuildGlobalDefaultsTab()) });
        tabs.Items.Add(new TabItem { Header = "Games", Content = ScrollSettings(BuildGamesSettingsTab()) });
        tabs.Items.Add(new TabItem { Header = "Profiles", Content = ScrollSettings(BuildProfilesSettingsTab()) });
        return Card(tabs);
    }

    private static ScrollViewer ScrollSettings(Control content)
    {
        var viewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };

        // A newly selected settings tab must start at its first section. Without an explicit
        // top alignment Avalonia can preserve the focused numeric editor's bring-into-view
        // offset, which hides the General heading and timeout controls on macOS.
        viewer.AttachedToVisualTree += (_, _) => viewer.Offset = new Vector(0, 0);
        return viewer;
    }

    private Control BuildGeneralSettingsTab()
    {
        var timeout = new NumericUpDown { Minimum = 15, Maximum = 180, Value = _viewModel.Settings.LaunchTimeoutSeconds, Increment = 5 };
        var delay = new NumericUpDown { Minimum = 0, Maximum = 60, Value = _viewModel.Settings.LaunchDelaySeconds, Increment = 1 };
        var preferred = new ComboBox { ItemsSource = new[] { "Auto", "Bloxstrap", "Standard" }, SelectedItem = _viewModel.Settings.PreferredLauncher };
        var continueOnFailure = new CheckBox { Content = "Continue after a failed account", IsChecked = _viewModel.Settings.ContinueOnFailure };
        var remember = new CheckBox { Content = "Remember account and preset selections", IsChecked = _viewModel.Settings.RememberSelections };
        var clearSessions = new CheckBox { Content = "Clear all Roblox browser sessions on next restart", IsChecked = _viewModel.Settings.ClearBrowserDataOnNextStart };
        var updates = new CheckBox { Content = "Enable automatic updates", IsChecked = _viewModel.Settings.UpdateChecksEnabled };
        var showGamePresetPanel = new CheckBox { Content = "Show game preset panel on the launch workspace", IsChecked = _viewModel.Settings.ShowGamePresetPanel };
        var channel = new ComboBox { ItemsSource = Enum.GetNames<UpdateChannel>(), SelectedItem = _viewModel.Settings.UpdateChannel.ToString(), MinWidth = 150 };
        var validation = new TextBlock { Foreground = DangerTextBrush, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Save general settings" };
        StyleButton(save);
        save.Click += async (_, _) =>
        {
            if (!Enum.TryParse<UpdateChannel>(channel.SelectedItem?.ToString(), true, out var selectedChannel))
            {
                validation.Text = "Choose a valid update channel.";
                return;
            }

            _viewModel.Settings.LaunchTimeoutSeconds = (int)(timeout.Value ?? 45);
            _viewModel.Settings.LaunchDelaySeconds = (int)(delay.Value ?? 0);
            _viewModel.Settings.PreferredLauncher = preferred.SelectedItem?.ToString() ?? "Auto";
            _viewModel.Settings.ContinueOnFailure = continueOnFailure.IsChecked == true;
            _viewModel.Settings.RememberSelections = remember.IsChecked == true;
            _viewModel.Settings.ClearBrowserDataOnNextStart = clearSessions.IsChecked == true;
            _viewModel.Settings.UpdateChecksEnabled = updates.IsChecked == true;
            _viewModel.Settings.UpdateChannel = selectedChannel;
            _viewModel.Settings.ShowGamePresetPanel = showGamePresetPanel.IsChecked == true;
            validation.Text = string.Empty;
            await SaveAsync();
            UpdatePresetRevealButton();
            RenderPage();
        };
        var checkNow = new Button { Content = "Check now" };
        StyleButton(checkNow, secondary: true);
        checkNow.Click += async (_, _) => await CheckForUpdateAsync();

        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        updateRow.Children.Add(updates);
        updateRow.Children.Add(new TextBlock { Text = "Channel", VerticalAlignment = VerticalAlignment.Center, Foreground = MutedTextBrush });
        updateRow.Children.Add(channel);
        updateRow.Children.Add(checkNow);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "General", FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Queue, launcher, storage, and update behavior.", Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Launch timeout (seconds)" }); panel.Children.Add(timeout);
        panel.Children.Add(new TextBlock { Text = "Delay between accounts (seconds)" }); panel.Children.Add(delay);
        panel.Children.Add(new TextBlock { Text = "Preferred launcher" }); panel.Children.Add(preferred);
        panel.Children.Add(continueOnFailure); panel.Children.Add(remember); panel.Children.Add(clearSessions); panel.Children.Add(showGamePresetPanel);
        panel.Children.Add(updateRow);
        panel.Children.Add(new TextBlock { Text = "Signed packages install automatically after validation. Unsigned packages always ask once before Apple Installer opens.", Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(validation); panel.Children.Add(save);
        return panel;
    }

    private Control BuildGlobalDefaultsTab()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Global Defaults", FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "These values apply first. Game and profile overrides are merged afterward.", Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(BuildGameSettingsEditor(_viewModel.Settings.GameSettings, false, settings => _viewModel.Settings.GameSettings = settings));
        return panel;
    }

    private Control BuildGamesSettingsTab()
    {
        var search = new TextBox { PlaceholderText = "Search games" };
        var matches = new ObservableCollection<GamePreset>(_viewModel.Presets);
        var list = new ListBox { ItemsSource = matches, MinWidth = 220, MaxHeight = 420 };
        var editor = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void SelectGame(GamePreset? preset)
        {
            if (preset is null || !GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out var normalized))
            {
                editor.Content = new TextBlock { Text = "Select a game preset with a valid Roblox URL.", Foreground = MutedTextBrush };
                return;
            }
            var current = _viewModel.Settings.GameOverrides.TryGetValue(normalized, out var value) ? value.Clone() : new GameSettings();
            editor.Content = BuildGameSettingsEditor(current, true, settings =>
            {
                if (settings.HasOverrides) _viewModel.Settings.GameOverrides[normalized] = settings;
                else _viewModel.Settings.GameOverrides.Remove(normalized);
            });
        }
        list.SelectionChanged += (_, _) => SelectGame(list.SelectedItem as GamePreset);
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? string.Empty;
            matches.Clear();
            foreach (var preset in _viewModel.Presets.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) matches.Add(preset);
        };
        if (matches.Count > 0) list.SelectedIndex = 0;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 16 };
        var left = new StackPanel { Spacing = 8, Children = { search, list } };
        layout.Children.Add(left); Grid.SetColumn(editor, 1); layout.Children.Add(editor);
        return new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Games", FontSize = 20, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Per-game overrides inherit from Global Defaults when set to Automatic.", Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap }, layout } };
    }

    private Control BuildProfilesSettingsTab()
    {
        var list = new ListBox { ItemsSource = _viewModel.Accounts, MinWidth = 220, MaxHeight = 420 };
        var editor = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void SelectProfile(AccountProfile? account)
        {
            if (account is null)
            {
                editor.Content = new TextBlock { Text = "Select an account profile.", Foreground = MutedTextBrush };
                return;
            }
            var current = account.GameSettings?.Clone() ?? new GameSettings();
            var showInClients = new CheckBox
            {
                Content = "Show Roblox client in the Clients panel",
                IsChecked = account.EmbedInClients
            };
            showInClients.Click += async (_, _) =>
            {
                account.EmbedInClients = showInClients.IsChecked == true;
                await SaveAsync();
            };
            editor.Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    showInClients,
                    BuildGameSettingsEditor(current, true, settings => account.GameSettings = settings.HasOverrides ? settings : null)
                }
            };
        }
        list.SelectionChanged += (_, _) => SelectProfile(list.SelectedItem as AccountProfile);
        if (_viewModel.Accounts.Count > 0) list.SelectedIndex = 0;
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), ColumnSpacing = 16 };
        layout.Children.Add(list); Grid.SetColumn(editor, 1); layout.Children.Add(editor);
        return new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Profiles", FontSize = 20, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = "Per-profile overrides are applied last and use Default to inherit.", Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap }, layout } };
    }

    private Control BuildGameSettingsEditor(GameSettings current, bool overrideMode, Action<GameSettings> onSave)
    {
        var automatic = overrideMode ? "Default" : "Automatic";
        var msaa = new ComboBox { ItemsSource = new[] { automatic, "Off", "2x", "4x", "8x" }, SelectedItem = current.MsaaSamples switch { 0 => "Off", 2 => "2x", 4 => "4x", 8 => "8x", _ => automatic } };
        var preserve = new ComboBox { ItemsSource = new[] { automatic, "Enabled", "Disabled" }, SelectedItem = current.PreserveRenderingQuality switch { true => "Enabled", false => "Disabled", _ => automatic } };
        var graphics = new ComboBox { ItemsSource = new[] { automatic }.Concat(Enumerable.Range(1, 10).Select(x => x.ToString())).ToArray(), SelectedItem = current.GraphicsQuality?.ToString() ?? automatic };
        var texture = new ComboBox { ItemsSource = new[] { automatic }.Concat(Enumerable.Range(0, 7).Select(x => x.ToString())).ToArray(), SelectedItem = current.TextureQuality?.ToString() ?? automatic };
        var fps = new ComboBox { ItemsSource = new[] { automatic, "60", "120", "144", "165", "240", "Custom" }, SelectedItem = current.FpsLimit is 60 or 120 or 144 or 165 or 240 ? current.FpsLimit.Value.ToString() : current.FpsLimit is null ? automatic : "Custom" };
        var fpsCustom = new TextBox { Text = current.FpsLimit is not null and not (60 or 120 or 144 or 165 or 240) ? current.FpsLimit.Value.ToString() : string.Empty, PlaceholderText = "30-1000", IsVisible = fps.SelectedItem?.ToString() == "Custom" };
        fps.SelectionChanged += (_, _) => fpsCustom.IsVisible = string.Equals(fps.SelectedItem?.ToString(), "Custom", StringComparison.Ordinal);
        var volume = new ComboBox { ItemsSource = new[] { automatic }.Concat(Enumerable.Range(0, 11).Select(x => x.ToString())).ToArray(), SelectedItem = current.MasterVolumeLevel?.ToString() ?? automatic };
        var flags = new TextBox { Text = current.AdvancedFlagsJson ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, PlaceholderText = "Optional JSON object of advanced engine flags" };
        var validation = new TextBlock { Foreground = DangerTextBrush, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = overrideMode ? "Save override" : "Save defaults" };
        StyleButton(save);
        save.Click += async (_, _) =>
        {
            if (!TryReadGameSettings(msaa, preserve, graphics, texture, fps, fpsCustom, volume, flags, automatic, out var parsed, out var error))
            {
                validation.Text = error;
                return;
            }
            validation.Text = string.Empty;
            onSave(parsed);
            await SaveAsync();
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = overrideMode ? "Override editor" : "Roblox defaults", FontSize = 17, FontWeight = FontWeight.SemiBold });
        AddLabeled(panel, "MSAA", msaa); AddLabeled(panel, "Rendering quality / client scaling", preserve);
        AddLabeled(panel, "Graphics quality", graphics); AddLabeled(panel, "Texture quality", texture);
        AddLabeled(panel, "FPS limit", fps); panel.Children.Add(fpsCustom);
        AddLabeled(panel, "Master volume", volume); AddLabeled(panel, "Advanced engine flags (JSON)", flags);
        panel.Children.Add(new TextBlock { Text = overrideMode ? "Automatic/Default removes this layer's value and restores inheritance." : "Automatic leaves Roblox's default behavior unchanged.", Foreground = MutedTextBrush, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(validation); panel.Children.Add(save);
        return panel;
    }

    private static void AddLabeled(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(control);
    }

    private static bool TryReadGameSettings(
        ComboBox msaa, ComboBox preserve, ComboBox graphics, ComboBox texture, ComboBox fps, TextBox fpsCustom,
        ComboBox volume, TextBox flags, string automatic, out GameSettings settings, out string error)
    {
        settings = new GameSettings(); error = string.Empty;
        var msaaValue = msaa.SelectedItem?.ToString();
        settings.MsaaSamples = msaaValue switch { "Off" => 0, "2x" => 2, "4x" => 4, "8x" => 8, _ => null };
        settings.PreserveRenderingQuality = preserve.SelectedItem?.ToString() switch { "Enabled" => true, "Disabled" => false, _ => null };
        settings.GraphicsQuality = ParseOptionalInt(graphics.SelectedItem?.ToString(), automatic, 1, 10, "graphics quality", ref error);
        settings.TextureQuality = ParseOptionalInt(texture.SelectedItem?.ToString(), automatic, 0, 6, "texture quality", ref error);
        var fpsChoice = fps.SelectedItem?.ToString();
        settings.FpsLimit = fpsChoice == "Custom" ? ParseOptionalInt(fpsCustom.Text, automatic, 30, 1000, "FPS", ref error) : ParseOptionalInt(fpsChoice, automatic, 30, 1000, "FPS", ref error);
        settings.MasterVolumeLevel = ParseOptionalInt(volume.SelectedItem?.ToString(), automatic, 0, 10, "master volume", ref error);
        settings.AdvancedFlagsJson = string.IsNullOrWhiteSpace(flags.Text) ? null : flags.Text.Trim();
        if (error.Length == 0 && !GameSettings.TryValidate(settings, out error)) return false;
        return error.Length == 0;
    }

    private static int? ParseOptionalInt(string? value, string automatic, int minimum, int maximum, string label, ref string error)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, automatic, StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            error = $"{label} must be between {minimum} and {maximum}.";
            return null;
        }
        return parsed;
    }

    private Control BuildPluginsPage(Action? refresh = null)
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
            var result = await _viewModel.PluginHost.InstallFromDirectoryAsync(source, userConfirmed: true);
            _viewModel.AppendActivity(result.Succeeded ? $"Installed plugin {result.PluginId}." : $"Plugin install rejected: {result.DiagnosticCode}.");
            if (refresh is null) RenderPage(); else refresh();
        };
        panel.Children.Add(install);
        var refreshPlugins = new Button { Content = "Refresh installed plugins" };
        refreshPlugins.Click += async (_, _) =>
        {
            if (_viewModel.PluginHost is null) return;
            var ids = await _viewModel.PluginHost.GetInstalledPluginIdsAsync();
            var running = await _viewModel.PluginHost.GetRunningPluginIdsAsync();
            _viewModel.AppendActivity(ids.Count == 0 ? "No macOS plugins are installed." : $"Installed plugins: {string.Join(", ", ids)}; running: {string.Join(", ", running)}");
            if (refresh is null) RenderPage(); else refresh();
        };
        panel.Children.Add(refreshPlugins);
        if (_viewModel.PluginHost is not null)
        {
            var ids = _viewModel.PluginHost.GetInstalledPluginIdsAsync().AsTask().GetAwaiter().GetResult();
            var running = _viewModel.PluginHost.GetRunningPluginIdsAsync().AsTask().GetAwaiter().GetResult();
            foreach (var id in ids)
            {
                var start = new Button { Content = running.Contains(id) ? "Running" : "Start", IsEnabled = !running.Contains(id) };
                start.Click += async (_, _) =>
                {
                    var result = await _viewModel.PluginHost.StartAsync(id, userConfirmed: true);
                    _viewModel.AppendActivity(result.Succeeded ? $"Started plugin {id}." : $"Plugin start rejected: {result.DiagnosticCode}.");
                    if (refresh is null) RenderPage(); else refresh();
                };
                var stop = new Button { Content = "Stop", IsEnabled = running.Contains(id) };
                stop.Click += async (_, _) =>
                {
                    var result = await _viewModel.PluginHost.StopAsync(id);
                    _viewModel.AppendActivity(result.Succeeded ? $"Stopped plugin {id}." : $"Plugin stop rejected: {result.DiagnosticCode}.");
                    if (refresh is null) RenderPage(); else refresh();
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
        panel.Children.Add(new TextBlock { Text = "Unsigned macOS packages require explicit approval in System Settings → Privacy & Security → Open Anyway. Roblox launch remains fail-closed unless the selected Roblox bundle passes its exact bundle ID, Developer ID, Gatekeeper, and designated-requirement checks. Team ID pinning is intentionally omitted for unsigned-build portability.", TextWrapping = TextWrapping.Wrap });
        return Card(panel);
    }

    private async Task CheckForUpdateAtStartupAsync()
    {
        if (_updateCheckStarted || !_viewModel.Settings.UpdateChecksEnabled || _updateSource is null || _viewModel.UpdateInstaller is null) return;
        _updateCheckStarted = true;
        await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateSource is null || _viewModel.UpdateInstaller is null)
        {
            _viewModel.AppendActivity("Updates are unavailable on this platform.");
            return;
        }

        try
        {
            var channel = _viewModel.Settings.UpdateChannel;
            var package = await _updateSource.DownloadLatestAsync(channel);
            if (package is null)
            {
                _viewModel.AppendActivity($"No newer {channel} macOS update is available.");
                return;
            }

            if (package.IsUnsigned)
            {
                if (_viewModel.UpdateInstaller is MacPkgUpdateInstaller macInstaller)
                {
                    var validationError = await macInstaller.ValidateAsync(package);
                    if (validationError is not null)
                    {
                        _viewModel.AppendActivity(
                            MacUpdateActivityFormatter.FormatUnsignedValidationRejection(
                                validationError,
                                macInstaller.CurrentPackageVersion));
                        return;
                    }
                }

                var approved = await ConfirmAsync("An unsigned development update was downloaded and verified. Install it now? Apple may require Open Anyway approval in Privacy & Security.");
                if (!approved)
                {
                    _viewModel.AppendActivity("Unsigned update deferred.");
                    return;
                }
            }

            var result = await _viewModel.UpdateInstaller.InstallAsync(package, userConfirmed: true);
            _viewModel.AppendActivity(result.Accepted
                ? $"{channel} update verified; Apple Installer opened. Restart RAM after installation completes."
                : $"{channel} update rejected: {result.DiagnosticCode}.");
        }
        catch (OperationCanceledException) { }
        catch (System.Net.Http.HttpRequestException exception)
            when (IsTlsFailure(exception))
        {
            _viewModel.AppendActivity("Update check skipped: TLS connection could not be established. Check the system date, certificates, proxy, or network and retry.");
        }
        catch (System.Net.Http.HttpRequestException)
        {
            _viewModel.AppendActivity("Update check skipped: the update service could not be reached. Retry when online.");
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Update check failed safely: {LaunchDiagnostics.SanitiseCode(exception.Message)}.");
        }
    }

    private static bool ContainsException<T>(Exception exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTlsFailure(System.Net.Http.HttpRequestException exception) =>
        ContainsException<System.Security.Authentication.AuthenticationException>(exception)
        || exception.Message.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase);

    private async Task RunLaunchQueueAsync(GamePreset? preset, IReadOnlyList<AccountProfile> accounts, string? customUrl = null)
    {
        if (_launches is null || preset is null || !DesktopPresetPolicy.TryResolveLaunchUrl(preset, customUrl, out var gameUrl)) { _viewModel.AppendActivity("Launch blocked: configure a validated Roblox bundle and a valid game preset."); return; }
        if (accounts.Count == 0) { _viewModel.AppendActivity("Launch blocked: favorite or open at least one account."); return; }
        _lastGameUrl = gameUrl;
        _lastLaunchPreset = preset;
        _lastCustomUrl = DesktopPresetPolicy.IsCustomUrlPreset(preset) ? customUrl : null;
        _viewModel.Queue.Clear();
        foreach (var account in accounts) _viewModel.Queue.Add(new LaunchQueueItem(account));
        _launchCancellation?.Dispose(); _launchCancellation = new CancellationTokenSource();
        string? launchedOverlayAccountId = null;
        UpdateActivityControls();
        try
        {
            foreach (var item in _viewModel.Queue)
            {
                _launchCancellation.Token.ThrowIfCancellationRequested();
                item.State = LaunchQueueState.Launching;
                item.Detail = OperatingSystem.IsMacOS()
                    ? "Waiting for Roblox Play control"
                    : "Waiting for Roblox launch URI";
                RefreshQueueSummary();
                GameSettings? gameSettings = preset.Settings;
                if (_viewModel.Settings.GameOverrides.TryGetValue(gameUrl, out var urlSettings))
                {
                    gameSettings = GameSettings.Resolve(new GameSettings(), gameSettings, urlSettings);
                }
                var scopedSettings = GameSettings.Resolve(
                    _viewModel.Settings.GameSettings,
                    gameSettings,
                    item.Account.GameSettings);
                var request = new CoreRobloxLaunchRequest(
                    item.Account.Id,
                    cancellationToken => new ValueTask<Uri>(CaptureLaunchUriAsync(item.Account, scopedSettings, gameUrl, cancellationToken)),
                    MaxAttempts: 3,
                    PreferredMacLevel: CoreMacLaunchLevel.ManagedSlots,
                    RobloxBundlePath: await DiscoverRobloxBundleAsync(),
                    VerificationTimeout: TimeSpan.FromSeconds(Math.Clamp(_viewModel.Settings.LaunchTimeoutSeconds, 15, 180)));
                LaunchResult result;
                var launchStartedUtc = DateTimeOffset.UtcNow;
                try
                {
                    result = await _launches.LaunchAsync(request, _launchCancellation.Token);
                }
                catch (OperationCanceledException) when (_launchCancellation is not null && !_launchCancellation.IsCancellationRequested)
                {
                    item.State = LaunchQueueState.Failed;
                    item.Detail = "Launch canceled by settings or browser failure";
                    RefreshQueueSummary();
                    _viewModel.AppendActivity($"{item.Label}: {item.Detail}.");
                    await AppendMacRobloxDiagnosticsAsync(launchStartedUtc);
                    if (!_viewModel.Settings.ContinueOnFailure) break;
                    continue;
                }
                catch (Exception exception)
                {
                    item.State = LaunchQueueState.Failed;
                    item.Detail = LaunchDiagnostics.SanitiseCode(exception.Message);
                    RefreshQueueSummary();
                    _viewModel.AppendActivity($"{item.Label}: launch failed safely ({item.Detail}).");
                    await AppendMacRobloxDiagnosticsAsync(launchStartedUtc);
                    if (!_viewModel.Settings.ContinueOnFailure) break;
                    continue;
                }
                if (OperatingSystem.IsMacOS())
                {
                    foreach (var attempt in result.Attempts)
                    {
                        _viewModel.AppendActivity(
                            $"{item.Label}: macOS native attempt {attempt.Attempt}={LaunchDiagnostics.SanitiseCode(attempt.DiagnosticCode)}.");
                    }
                }
                item.State = result.Succeeded ? LaunchQueueState.Running : LaunchQueueState.Failed;
                item.Detail = result.Succeeded ? "Verified process started" : result.FailureKind.ToString();
                if (result.Succeeded && item.Account.EmbedInClients) launchedOverlayAccountId = item.Account.Id;
                RefreshQueueSummary();
                _viewModel.AppendActivity($"{item.Label}: {item.Detail}.");
                if (!result.Succeeded)
                {
                    await AppendMacRobloxDiagnosticsAsync(launchStartedUtc);
                }
                if (!result.Succeeded && !_viewModel.Settings.ContinueOnFailure) break;
                if (_viewModel.Settings.LaunchDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_viewModel.Settings.LaunchDelaySeconds), _launchCancellation.Token);
            }
        }
        catch (OperationCanceledException) { foreach (var item in _viewModel.Queue.Where(x => x.State == LaunchQueueState.Waiting || x.State == LaunchQueueState.Launching)) { item.State = LaunchQueueState.Canceled; item.Detail = "Canceled"; } _viewModel.AppendActivity("Launch queue canceled."); }
        catch (Exception exception) { _viewModel.AppendActivity($"Launch queue failed safely: {exception.Message}"); }
        finally
        {
            _launchCancellation?.Dispose();
            _launchCancellation = null;
            await SaveAsync();
            if (launchedOverlayAccountId is not null && _clientOverlay is not null)
            {
                _selectedClientAccountId = launchedOverlayAccountId;
                SelectPage("clients");
            }
            else
            {
                RenderPage();
            }
        }
    }

    private async Task RetryFailedLaunchesAsync()
    {
        if (_launchCancellation is not null || _lastLaunchPreset is null) return;
        var failed = _viewModel.Queue.Where(item => item.State == LaunchQueueState.Failed).Select(item => item.Account).ToArray();
        if (failed.Length == 0) return;
        await RunLaunchQueueAsync(_lastLaunchPreset, failed, _lastCustomUrl);
    }

    private async Task<Uri> CaptureLaunchUriAsync(
        AccountProfile account,
        GameSettings? scopedSettings,
        string gameUrl,
        CancellationToken cancellationToken)
    {
        RobloxSettingsApplyResult? settingsResult = null;
        if (_viewModel.RobloxSettings is not null && scopedSettings is not null && scopedSettings.HasOverrides)
        {
            settingsResult = await _viewModel.RobloxSettings.ApplyAsync(scopedSettings, cancellationToken);
            if (!settingsResult.Succeeded)
            {
                await _viewModel.RobloxSettings.RecoverAsync(cancellationToken);
            }
        }

        return await InvokeOnUiThreadAsync(async () =>
        {
            if (settingsResult is not null)
            {
                _viewModel.AppendActivity($"{account.Label}: Roblox settings applied={settingsResult.Applied.Count}, skipped={settingsResult.Skipped.Count}.");
                if (!settingsResult.Succeeded)
                    throw new InvalidOperationException(settingsResult.DiagnosticCode ?? "roblox-settings-apply-failed");
            }

            _viewModel.SelectedAccount = account;
            var isMacOS = OperatingSystem.IsMacOS();
            if (!await OpenAccountAsync(account, navigate: !isMacOS)) throw new InvalidOperationException("browser-session-unavailable");
            if (isMacOS)
            {
                RobloxPlayControlStatus? previousStatus = null;
                var coordinator = new MacBrowserLaunchCoordinator(
                    _browserSessions,
                    status =>
                    {
                        if (status == previousStatus) return;
                        previousStatus = status;
                        _viewModel.AppendActivity($"{account.Label}: {DescribeMacPlayStatus(status)}.");
                    });
                var launchUri = await coordinator.CaptureAsync(
                    account.Id,
                    new Uri(gameUrl),
                    TimeSpan.FromSeconds(Math.Clamp(_viewModel.Settings.LaunchTimeoutSeconds, 15, 180)),
                    cancellationToken);
                _viewModel.AppendActivity($"{account.Label}: Roblox launch handoff captured; native launch starting.");
                return launchUri;
            }

            var pending = _browserSessions.BeginLaunchCapture(account.Id, cancellationToken);
            await _browserSessions.NavigateAsync(account.Id, new Uri(gameUrl), cancellationToken);
            var navigation = await pending.ConfigureAwait(true);
            if (!navigation.TryConsumeLaunchUri(out var uri) || uri is null) throw new InvalidOperationException("No Roblox launch URI was captured.");
            return uri;
        }, cancellationToken);
    }

    private static Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) return operation();
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                registration.Dispose();
            }
        }, Avalonia.Threading.DispatcherPriority.Normal);
        return completion.Task;
    }

    private int GetSelectedTimeoutSeconds()
    {
        var selected = _activityTimeout.SelectedItem?.ToString()?.TrimEnd('s');
        return int.TryParse(selected, out var seconds) ? Math.Clamp(seconds, 15, 180) : 45;
    }

    private async Task<string?> DiscoverRobloxBundleAsync()
    {
        var discovery = new MacBundleDiscovery();
        var bundle = await discovery.DiscoverAsync();
        return bundle?.BundlePath;
    }

    private async Task LogMacRobloxClientVersionAsync()
    {
        try
        {
            var bundle = await new MacBundleDiscovery().DiscoverAsync();
            _viewModel.AppendActivity(MacRobloxDiagnostics.DescribeClient(bundle));
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Roblox client diagnostics unavailable: {LaunchDiagnostics.SanitiseCode(exception.Message)}.");
        }
    }

    private async Task AppendMacRobloxDiagnosticsAsync(DateTimeOffset processStartUtc)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var artifactDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RobloxAltClient",
                "diagnostics");
            var diagnostics = await Task.Run(() => MacRobloxDiagnostics.Collect(processStartUtc, artifactDirectory: artifactDirectory));
            foreach (var summary in diagnostics.Summary)
            {
                _viewModel.AppendActivity(summary);
            }

            if (diagnostics.ArtifactPath is not null)
            {
                _viewModel.AppendActivity($"Redacted Roblox diagnostic tail saved to {diagnostics.ArtifactPath}.");
            }
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Roblox log diagnostics unavailable: {LaunchDiagnostics.SanitiseCode(exception.Message)}.");
        }
    }

    private static string DescribeMacPlayStatus(RobloxPlayControlStatus status) => status switch
    {
        RobloxPlayControlStatus.Clicked => "Play control clicked",
        RobloxPlayControlStatus.NotFound => "Waiting for Roblox Play control",
        RobloxPlayControlStatus.WrongOrigin => "Waiting for Roblox page to finish loading",
        _ => "Roblox Play control is not ready"
    };

    private void AppendNavigationDiagnostic(string message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            _viewModel.AppendActivity(message);
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _viewModel.AppendActivity(message),
            Avalonia.Threading.DispatcherPriority.Normal);
    }

    private async Task<bool> OpenAccountAsync(AccountProfile account, bool navigate = true)
    {
        var activation = Interlocked.Increment(ref _browserActivationVersion);
        return await ActivateBrowserSessionAsync(account, activation, navigate);
    }

    private async Task<bool> ActivateBrowserSessionAsync(AccountProfile account, long activation, bool navigate)
    {
        try
        {
            await _browserSessions.CreateAsync(account.Id, account.Label);
            if (activation != Volatile.Read(ref _browserActivationVersion) || _viewModel.SelectedAccount?.Id != account.Id) return false;
            _browserHost.Content = _browserSessions.GetView(account.Id);
            if (navigate)
                await _browserSessions.NavigateAsync(account.Id, new Uri("https://www.roblox.com/home"));
            _viewModel.AppendActivity($"Opened isolated Roblox session for {account.Label}.");
            return true;
        }
        catch (Exception exception)
        {
            if (activation == Volatile.Read(ref _browserActivationVersion)) _viewModel.AppendActivity($"Browser session unavailable: {LaunchDiagnostics.SanitiseCode(exception.Message)}.");
            return false;
        }
    }

    private async Task ValidateBrowserStartupAsync(AccountProfile account)
    {
        if (!await OpenAccountAsync(account, navigate: false))
        {
            RequestValidationShutdown(1);
            return;
        }

        var view = _browserSessions.GetView(account.Id);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (view.TryGetPlatformHandle() is not null)
            {
                RequestValidationShutdown();
                return;
            }
            await Task.Delay(100);
        }

        _viewModel.AppendActivity("Browser startup validation did not create a native WebView adapter.");
        RequestValidationShutdown(1);
    }

    private static void RequestValidationShutdown(int exitCode = 0)
    {
        Environment.ExitCode = exitCode;
        Avalonia.Threading.Dispatcher.UIThread.Post(static () =>
        {
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task ShowLoginAsync()
    {
        SelectPage("browser");
        if (_viewModel.SelectedAccount is null) return;
        if (!await OpenAccountAsync(_viewModel.SelectedAccount)) return;
        try
        {
            await _browserSessions.NavigateAsync(_viewModel.SelectedAccount.Id, new Uri("https://www.roblox.com/login"));
        }
        catch (Exception exception)
        {
            _viewModel.AppendActivity($"Browser navigation failed: {LaunchDiagnostics.SanitiseCode(exception.Message)}.");
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_viewModel.Settings.RememberSelections)
            {
                _viewModel.Settings.LastSelectedProfileIds = _viewModel.Accounts
                    .Where(account => _queueSelectedAccounts.Contains(account.Id))
                    .Select(account => account.Id)
                    .ToList();
            }
            else
            {
                _viewModel.Settings.LastSelectedProfileIds.Clear();
            }

            await _viewModel.SaveAsync();
        }
        catch (Exception exception) { _viewModel.AppendActivity($"Could not save local data: {exception.Message}"); }
    }

    private async Task MoveSelectedAccountAsync(int offset)
    {
        if (_viewModel.SelectedAccount is null) return;
        var index = _viewModel.Accounts.IndexOf(_viewModel.SelectedAccount);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _viewModel.Accounts.Count) return;
        _viewModel.Accounts.Move(index, target);
        for (var i = 0; i < _viewModel.Accounts.Count; i++) _viewModel.Accounts[i].SortOrder = i;
        await SaveAsync();
    }

    private static Border Card(Control child) => new()
    {
        Background = SurfaceBrush,
        Padding = new Thickness(18),
        Margin = new Thickness(0, 0, 0, 0),
        BorderBrush = ControlBorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = child
    };

    private async Task<(string Label, string Group, bool ShowInClients)?> PromptAsync(
        string title,
        string labelValue,
        string groupValue,
        bool showInClientsValue)
    {
        var dialog = new Window { Title = title, Width = 420, Height = 280, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var label = new TextBox { Text = labelValue, PlaceholderText = "Account label" };
        var group = new TextBox { Text = groupValue, PlaceholderText = "Group (optional)" };
        var showInClients = new CheckBox
        {
            Content = "Show Roblox client in the Clients panel",
            IsChecked = showInClientsValue
        };
        var result = new TaskCompletionSource<(string, string, bool)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ok = new Button { Content = "Save" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(label.Text))
            {
                result.TrySetResult((label.Text.Trim(), group.Text?.Trim() ?? string.Empty, showInClients.IsChecked == true));
                dialog.Close();
            }
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { label, group, showInClients, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } } } };
        dialog.Show(this);
        return await result.Task;
    }

    private async Task<(string Name, string Url)?> PromptPresetAsync(string title, string nameValue, string urlValue)
    {
        var dialog = new Window { Title = title, Width = 500, Height = 260, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Text = nameValue, PlaceholderText = "Preset name" };
        var url = new TextBox { Text = urlValue, PlaceholderText = "Roblox game URL" };
        var result = new TaskCompletionSource<(string, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ok = new Button { Content = "Save" };
        var cancel = new Button { Content = "Cancel" };
        ok.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(url.Text))
            {
                result.TrySetResult((name.Text.Trim(), url.Text.Trim()));
                dialog.Close();
            }
        };
        cancel.Click += (_, _) => { result.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Game preset", FontWeight = FontWeight.SemiBold },
                name,
                url,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } }
            }
        };
        dialog.Show(this);
        return await result.Task;
    }

    private sealed record ClientTabItem(string AccountId, string Label, RobloxWindowInfo Window);

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

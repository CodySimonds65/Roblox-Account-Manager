using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using System.Text.Json;
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
    private Button? _launchSelected;
    private Button? _queueLaunch;
    private readonly StackPanel _queueSummary = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
    private readonly ListBox _accountsRail = new();
    private readonly ComboBox _moreNavigation = new();
    private readonly Dictionary<string, Border> _accountCards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _accountChecks = new(StringComparer.Ordinal);
    private readonly AvaloniaAccountBrowserSessionService _browserSessions;
    private readonly SerializedLaunchCoordinator? _launches;
    private readonly CoreClientWindowManager? _clients;
    private CancellationTokenSource? _launchCancellation;
    private string? _lastGameUrl;
    private GamePreset? _lastLaunchPreset;
    private bool _suppressAccountSelection;
    private long _browserActivationVersion;
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
        Width = 1380;
        Height = 860;
        MinWidth = 1080;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = AppBackgroundBrush;
        Foreground = TextBrush;

        _pageTitle.FontSize = 28;
        _pageTitle.FontWeight = FontWeight.Bold;
        _pageTitle.Foreground = TextBrush;
        _pageDescription.TextWrapping = TextWrapping.Wrap;
        _pageDescription.Foreground = MutedTextBrush;
        _activity.IsReadOnly = true;
        _activity.AcceptsReturn = true;
        _activity.TextWrapping = TextWrapping.NoWrap;
        _activity.MinHeight = 96;
        _activity.VerticalAlignment = VerticalAlignment.Stretch;
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
        AddHeaderAction(actions, "Browse", "browser");
        AddHeaderAction(actions, "Plugins", "plugins");
        AddHeaderAction(actions, "Settings", "settings");
        AddHeaderAction(actions, "Diagnostics", "diagnostics");
        _moreNavigation.ItemsSource = _viewModel.Pages.Where(page => page.Key is not ("accounts" or "browser" or "plugins" or "settings" or "diagnostics")).ToArray();
        _moreNavigation.ItemTemplate = new FuncDataTemplate<NavigationItemViewModel>((page, _) => new TextBlock { Text = page?.Title ?? string.Empty });
        _moreNavigation.Width = 128;
        _moreNavigation.SelectedIndex = -1;
        _moreNavigation.SelectionChanged += (_, _) =>
        {
            if (_moreNavigation.SelectedItem is NavigationItemViewModel page)
            {
                SelectPage(page.Key);
                _moreNavigation.SelectedIndex = -1;
            }
        };
        actions.Children.Add(_moreNavigation);
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
        return header;
    }

    private void AddHeaderAction(Panel panel, string title, string pageKey)
    {
        var button = new Button { Content = title, Padding = new Thickness(13, 7) };
        StyleButton(button, secondary: true);
        button.Click += (_, _) => SelectPage(pageKey);
        panel.Children.Add(button);
    }

    private Border BuildSidebar()
    {
        _accountsRail.ItemsSource = _viewModel.Accounts;
        _accountsRail.Height = 254;
        _accountsRail.Background = Brushes.Transparent;
        _accountsRail.BorderThickness = new Thickness(0);
        _accountsRail.SelectionMode = SelectionMode.Multiple;
        _accountsRail.ItemTemplate = new FuncDataTemplate<AccountProfile>((account, _) => BuildAccountRailRow(account));
        _accountsRail.SelectionChanged += async (_, args) =>
        {
            if (_suppressAccountSelection) return;
            foreach (var added in args.AddedItems.OfType<AccountProfile>()) _queueSelectedAccounts.Add(added.Id);
            foreach (var removed in args.RemovedItems.OfType<AccountProfile>()) _queueSelectedAccounts.Remove(removed.Id);
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
            await OpenAccountAsync(account);
            SelectPage("accounts");
        };

        var shell = new StackPanel { Spacing = 0 };
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
        shell.Children.Add(profilesHeader);
        shell.Children.Add(_accountsRail);

        var accountActions = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        var add = new Button { Content = "＋  Add account profile", HorizontalContentAlignment = HorizontalAlignment.Center };
        StyleButton(add);
        add.Click += async (_, _) =>
        {
            var values = await PromptAsync("New account profile", "Roblox account", string.Empty);
            if (values is null) return;
            _viewModel.Accounts.Add(new AccountProfile { Label = values.Value.Label, Group = values.Value.Group, SortOrder = _viewModel.Accounts.Count });
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
            var values = await PromptAsync("Edit account profile", account.Label, account.Group);
            if (values is null) return;
            account.Label = values.Value.Label;
            account.Group = values.Value.Group;
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
        selectAll.Click += (_, _) =>
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
            RefreshAccountRail();
            RenderPage();
        };
        selectionActions.Children.Add(selectAll);
        var selectNone = new Button { Content = "Select none", Padding = new Thickness(10, 6) };
        StyleButton(selectNone, secondary: true);
        selectNone.Click += (_, _) =>
        {
            _queueSelectedAccounts.Clear();
            _viewModel.SelectedAccount = null;
            Interlocked.Increment(ref _browserActivationVersion);
            _suppressAccountSelection = true;
            try { _accountsRail.SelectedItems?.Clear(); }
            finally { _suppressAccountSelection = false; }
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
        shell.Children.Add(accountActions);

        return new Border
        {
            Background = SurfaceBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(17),
            Child = new ScrollViewer
            {
                Content = shell,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
    }

    private Control BuildAccountRailRow(AccountProfile account)
    {
        var open = new CheckBox { Content = new StackPanel { Spacing = 1, Children = { new TextBlock { Text = account.Label, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, new TextBlock { Text = string.IsNullOrWhiteSpace(account.Group) ? "" : account.Group, FontSize = 10, Foreground = MutedTextBrush, TextTrimming = TextTrimming.CharacterEllipsis } } }, IsChecked = _queueSelectedAccounts.Contains(account.Id), Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        _accountChecks[account.Id] = open;
        open.Click += async (_, _) =>
        {
            if (open.IsChecked == true) _queueSelectedAccounts.Add(account.Id);
            else _queueSelectedAccounts.Remove(account.Id);
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
                await OpenAccountAsync(account);
            }
            else if (wasActive)
            {
                _viewModel.SelectedAccount = remaining;
                if (remaining is null)
                {
                    Interlocked.Increment(ref _browserActivationVersion);
                }
                else
                {
                    UpdateAccountSelectionVisuals();
                    await OpenAccountAsync(remaining);
                }
            }
            UpdateAccountSelectionVisuals();
            SelectPage("accounts");
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
        return new Border { Background = SurfaceBrush, BorderBrush = ControlBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(13), Child = layout };
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

    private void SelectPage(string pageKey)
    {
        var page = _viewModel.Pages.FirstOrDefault(candidate => string.Equals(candidate.Key, pageKey, StringComparison.Ordinal));
        if (page is null) return;
        _viewModel.SelectedPage = page;
        RenderPage();
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
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DesktopShellViewModel.Activity)) _activity.Text = _viewModel.Activity;
            };
            _activity.Text = _viewModel.Activity;
            RenderPage();
            if (_viewModel.Accounts.Count > 0)
            {
                _viewModel.SelectedAccount = _viewModel.Accounts[0];
                _queueSelectedAccounts.Add(_viewModel.SelectedAccount.Id);
                _suppressAccountSelection = true;
                try { _accountsRail.SelectedItems?.Add(_viewModel.SelectedAccount); }
                finally { _suppressAccountSelection = false; }
                UpdateAccountSelectionVisuals();
                await OpenAccountAsync(_viewModel.SelectedAccount);
                RenderPage();
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
        DetachBrowserHost();
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
            "updates" => BuildUpdatesPage(),
            "diagnostics" => BuildDiagnosticsPage(),
            _ => new TextBlock { Text = "Select a page." }
        };
        UpdateActivityControls();
        var activation = Interlocked.Increment(ref _browserActivationVersion);
        if (isWorkspace && _viewModel.SelectedAccount is not null)
            _ = ActivateBrowserSessionAsync(_viewModel.SelectedAccount, activation);
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
        var presetMatches = new ObservableCollection<GamePreset>(_viewModel.Presets);
        var presetPicker = new ComboBox
        {
            ItemsSource = presetMatches,
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
            presetMatches.Clear();
            foreach (var match in _viewModel.Presets.Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) presetMatches.Add(match);
            if (presetPicker.SelectedItem is not GamePreset selected || !presetMatches.Contains(selected)) presetPicker.SelectedItem = presetMatches.FirstOrDefault();
        };
        var presetActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 5 };
        presetActions.Children.Add(presetPicker);
        var presetActionNames = new[] { ("＋", "Add preset", "add"), ("−", "Remove preset", "remove"), ("✎", "Edit preset", "edit"), ("⧉", "Duplicate preset", "duplicate"), ("⇩", "Import presets", "import"), ("⇧", "Export presets", "export") };
        for (var i = 0; i < presetActionNames.Length; i++)
        {
            var action = new Button { Content = presetActionNames[i].Item1, Width = 34, Height = 34, Padding = new Thickness(0), IsEnabled = presetActionNames[i].Item3 is "add" or "import" or "export" || presetActionNames[i].Item3 is "duplicate" && selectedPreset?.Url is not null && selectedPreset.Url.Length > 0 || presetActionNames[i].Item3 is "remove" or "edit" && selectedPreset is { IsBuiltIn: false } };
            ToolTip.SetTip(action, presetActionNames[i].Item2);
            StyleButton(action, secondary: true);
            var actionKey = presetActionNames[i].Item3;
            action.Click += async (_, _) => await HandlePresetActionAsync(actionKey);
            Grid.SetColumn(action, i + 1);
            presetActions.Children.Add(action);
        }

        var consent = new CheckBox
        {
            Content = "I consent to macOS multi-instance semaphore changes",
            IsChecked = _viewModel.Settings.MultiInstanceConsentGranted,
            Foreground = TextBrush,
            Margin = new Thickness(0, 7, 0, 0)
        };
        var presetControls = new StackPanel { Spacing = 5 };
        presetControls.Children.Add(new TextBlock { Text = "GAME PRESET", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = MutedTextBrush });
        presetControls.Children.Add(presetSearch);
        presetControls.Children.Add(presetActions);
        presetControls.Children.Add(consent);

        var customUrlText = new TextBlock
        {
            Text = selectedPreset is null ? "Choose a game preset" : selectedPreset.Url,
            Foreground = MutedTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260
        };
        presetPicker.SelectionChanged += (_, _) => customUrlText.Text = (presetPicker.SelectedItem as GamePreset)?.Url ?? "Choose a game preset";

        var customUrl = new Border
        {
            Background = InputBrush,
            BorderBrush = ControlBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 10),
            Child = customUrlText
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
            await RunLaunchQueueAsync(consent.IsChecked == true, presetPicker.SelectedItem as GamePreset, accounts);
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
        browse.Click += (_, _) => SelectPage("browser");
        var clients = new Button { Content = "Clients", Padding = new Thickness(14, 5) };
        StyleButton(clients, secondary: true);
        clients.Click += (_, _) => SelectPage("clients");
        sessionActions.Children.Add(browse);
        sessionActions.Children.Add(clients);
        Grid.SetColumn(sessionActions, 1);
        sessionHeader.Children.Add(sessionActions);

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
        else
        {
            _browserHost.Content = new TextBlock { Text = "Opening isolated Roblox session...", Foreground = MutedTextBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        var browserBody = new Grid { RowDefinitions = new RowDefinitions("46,3,*"), Background = InputBrush, ClipToBounds = true };
        browserBody.Children.Add(sessionHeader);
        var progress = new Border { Background = AccentBrush, Height = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(progress, 1);
        browserBody.Children.Add(progress);
        Grid.SetRow(_browserHost, 2);
        browserBody.Children.Add(_browserHost);
        var browserCard = new Border { Background = SurfaceBrush, BorderBrush = ControlBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = browserBody };

        var workspace = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 14, ClipToBounds = true };
        workspace.Children.Add(presetBar);
        Grid.SetRow(browserCard, 1);
        workspace.Children.Add(browserCard);
        var hint = new TextBlock { Text = "Sessions stay isolated and local to this PC. Browser data is never included in exports.", FontSize = 11, Foreground = MutedTextBrush, Margin = new Thickness(3, 0, 3, 0) };
        Grid.SetRow(hint, 2);
        workspace.Children.Add(hint);
        return workspace;
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
        var consent = new CheckBox { Content = "I consent to macOS multi-instance semaphore changes", IsChecked = _viewModel.Settings.MultiInstanceConsentGranted };
        var launch = new Button { Content = _launches is null ? "Launch unavailable until Roblox trust is configured" : "Launch selected accounts", IsEnabled = _launches is not null && _launchCancellation is null };
        _queueLaunch = launch;
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
        panel.Children.Add(new TextBlock { Text = "Unsigned macOS packages require explicit approval in System Settings → Privacy & Security → Open Anyway. Roblox launch remains fail-closed unless the selected Roblox bundle passes its exact bundle ID, Developer ID, Gatekeeper, and designated-requirement checks. Team ID pinning is intentionally omitted for unsigned-build portability.", TextWrapping = TextWrapping.Wrap });
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
        if (_launches is null || preset is null || !GamePreset.TryNormalizeRobloxGameUrl(preset.Url, out var gameUrl)) { _viewModel.AppendActivity("Launch blocked: configure a validated Roblox bundle and a valid game preset."); return; }
        if (accounts.Count == 0) { _viewModel.AppendActivity("Launch blocked: favorite or open at least one account."); return; }
        _viewModel.Settings.MultiInstanceConsentGranted = true;
        _lastGameUrl = gameUrl;
        _lastLaunchPreset = preset;
        _viewModel.Queue.Clear();
        foreach (var account in accounts) _viewModel.Queue.Add(new LaunchQueueItem(account));
        _launchCancellation?.Dispose(); _launchCancellation = new CancellationTokenSource();
        UpdateActivityControls();
        try
        {
            foreach (var item in _viewModel.Queue)
            {
                _launchCancellation.Token.ThrowIfCancellationRequested();
                item.State = LaunchQueueState.Launching; item.Detail = "Waiting for Roblox launch URI";
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
                    _viewModel.SelectedAccount = item.Account;
                    if (!await OpenAccountAsync(item.Account)) throw new InvalidOperationException("browser-session-unavailable");
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
                    RefreshQueueSummary();
                    _viewModel.AppendActivity($"{item.Label}: {item.Detail}.");
                    if (!_viewModel.Settings.ContinueOnFailure) break;
                    continue;
                }
                catch (Exception exception)
                {
                    item.State = LaunchQueueState.Failed;
                    item.Detail = LaunchDiagnostics.SanitiseCode(exception.Message);
                    RefreshQueueSummary();
                    _viewModel.AppendActivity($"{item.Label}: launch failed safely ({item.Detail}).");
                    if (!_viewModel.Settings.ContinueOnFailure) break;
                    continue;
                }
                item.State = result.Succeeded ? LaunchQueueState.Running : LaunchQueueState.Failed;
                item.Detail = result.Succeeded ? "Verified process started" : result.FailureKind.ToString();
                RefreshQueueSummary();
                _viewModel.AppendActivity($"{item.Label}: {item.Detail}.");
                if (!result.Succeeded && !_viewModel.Settings.ContinueOnFailure) break;
                if (_viewModel.Settings.LaunchDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(_viewModel.Settings.LaunchDelaySeconds), _launchCancellation.Token);
            }
        }
        catch (OperationCanceledException) { foreach (var item in _viewModel.Queue.Where(x => x.State == LaunchQueueState.Waiting || x.State == LaunchQueueState.Launching)) { item.State = LaunchQueueState.Canceled; item.Detail = "Canceled"; } _viewModel.AppendActivity("Launch queue canceled."); }
        catch (Exception exception) { _viewModel.AppendActivity($"Launch queue failed safely: {exception.Message}"); }
        finally { _launchCancellation?.Dispose(); _launchCancellation = null; await SaveAsync(); RenderPage(); }
    }

    private async Task RetryFailedLaunchesAsync()
    {
        if (_launchCancellation is not null || _lastLaunchPreset is null) return;
        var failed = _viewModel.Queue.Where(item => item.State == LaunchQueueState.Failed).Select(item => item.Account).ToArray();
        if (failed.Length == 0) return;
        await RunLaunchQueueAsync(_viewModel.Settings.MultiInstanceConsentGranted, _lastLaunchPreset, failed);
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

    private async Task<bool> OpenAccountAsync(AccountProfile account)
    {
        var activation = Interlocked.Increment(ref _browserActivationVersion);
        return await ActivateBrowserSessionAsync(account, activation);
    }

    private async Task<bool> ActivateBrowserSessionAsync(AccountProfile account, long activation)
    {
        try
        {
            await _browserSessions.CreateAsync(account.Id, account.Label);
            if (activation != Volatile.Read(ref _browserActivationVersion) || _viewModel.SelectedAccount?.Id != account.Id) return false;
            _browserHost.Content = _browserSessions.GetView(account.Id);
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
        try { await _viewModel.SaveAsync(); }
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Launch;
using RobloxAccountManager.Desktop.Services;
using RobloxAccountManager.Desktop.ViewModels;

namespace RobloxAccountManager.Desktop.Views;

/// <summary>
/// Minimal code-built shell keeps the migration buildable while feature screens
/// move to view models. Platform services are injected at the composition root,
/// not reached from view code-behind.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly DesktopShellViewModel _viewModel;
    private readonly ContentControl _content;
    private readonly TextBlock _pageTitle;
    private readonly TextBlock _pageDescription;
    private readonly StackPanel _capabilityPanel;
    private readonly AvaloniaAccountBrowserSessionService _browserSessions;
    private readonly SerializedLaunchCoordinator? _launches;
    private readonly IClientWindowManager? _clients;

    public MainWindow(
        DesktopShellViewModel viewModel,
        AvaloniaAccountBrowserSessionService browserSessions,
        SerializedLaunchCoordinator? launches = null,
        IClientWindowManager? clients = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _browserSessions = browserSessions ?? throw new ArgumentNullException(nameof(browserSessions));
        _launches = launches;
        _clients = clients;
        DataContext = _viewModel;
        Title = "Roblox Account Manager";
        Width = 1180;
        Height = 760;
        MinWidth = 860;
        MinHeight = 560;

        var navigation = new ListBox
        {
            Width = 210,
            Background = Brushes.Transparent,
            ItemsSource = _viewModel.Pages,
            SelectedItem = _viewModel.SelectedPage,
            Margin = new Thickness(12)
        };
        navigation.SelectionChanged += (_, _) =>
        {
            if (navigation.SelectedItem is NavigationItemViewModel page)
            {
                _viewModel.SelectedPage = page;
                UpdatePage();
            }
        };

        _pageTitle = new TextBlock { FontSize = 28, FontWeight = FontWeight.Bold };
        _pageDescription = new TextBlock { FontSize = 15, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        _capabilityPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        _content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

        var header = new StackPanel { Spacing = 6 };
        header.Children.Add(_pageTitle);
        header.Children.Add(_pageDescription);
        header.Children.Add(new TextBlock { Text = _viewModel.PlatformLabel, FontSize = 12, Opacity = 0.65 });
        header.Children.Add(_capabilityPanel);

        var page = new StackPanel { Spacing = 12, Margin = new Thickness(28) };
        page.Children.Add(header);
        page.Children.Add(_content);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(navigation);
        var pageScrollViewer = new ScrollViewer { Content = page };
        Grid.SetColumn(pageScrollViewer, 1);
        grid.Children.Add(pageScrollViewer);
        Content = grid;
        UpdatePage();
    }

    private void UpdatePage()
    {
        _pageTitle.Text = _viewModel.SelectedPage.Title;
        _pageDescription.Text = _viewModel.PageStatus;
        _capabilityPanel.Children.Clear();
        foreach (var capability in _viewModel.CurrentPageCapabilities)
        {
            var color = capability.Status == CapabilityStatus.Supported ? Brushes.ForestGreen : Brushes.DarkOrange;
            _capabilityPanel.Children.Add(new Border
            {
                BorderBrush = color,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Child = new TextBlock
                {
                    Text = $"{capability.Name}: {capability.Description}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = color
                }
            });
        }

        _content.Content = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(18),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = _viewModel.SelectedPage.Key switch
                {
                    "browser" => "NativeWebView account sessions are registered. Windows keeps the existing WebView2 data root/profile ids; macOS uses account GUID WKWebsiteDataStore identifiers.",
                    "queue" when _launches is not null => "The serialized macOS launch coordinator is registered. It requires explicit consent, a trusted Roblox Developer Team ID, and a fresh one-use Roblox URI for each attempt.",
                    "queue" => "Launching is disabled until this build is configured with Roblox's verified Developer Team ID. Authentication tickets are never passed to an untrusted bundle.",
                    "clients" when _clients is not null => "The macOS external-client manager is registered. Accessibility is required only for focus and tiling; close operations require a verified RAM-managed identity.",
                    "clients" => "Roblox clients are managed as normal external windows on macOS. Launching and closing verified processes does not require Accessibility permission.",
                    _ => "This Avalonia shell is ready for the shared feature view model."
                },
                TextWrapping = TextWrapping.Wrap
            }
        };
    }
}

using System.Windows;
using System.Windows.Controls;
using RobloxAltClient.Plugins;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class PluginsWindow : Window
{
    private PluginRuntime Runtime => ((App)Application.Current).PluginRuntime;

    public PluginsWindow()
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        Loaded += (_, _) => Refresh();
        Runtime.Changed += Runtime_Changed;
    }

    private void Runtime_Changed(object? sender, EventArgs e) => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        InstalledList.Items.Clear();
        foreach (var plugin in Runtime.Installed)
        {
            var card = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = $"{plugin.Manifest.Name}  v{plugin.Manifest.Version}", FontWeight = FontWeights.SemiBold });
            details.Children.Add(new TextBlock { Text = plugin.Manifest.Description, Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 8, 0) });
            details.Children.Add(new TextBlock { Text = $"Publisher: {plugin.Manifest.Publisher} · Capabilities: {plugin.GrantedCapabilities.Count}/{plugin.Manifest.Capabilities.Count}", Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            details.Children.Add(new TextBlock { Text = plugin.LastError ?? (plugin.IsRunning ? "Running" : "Stopped"), Foreground = plugin.LastError is null ? (System.Windows.Media.Brush)FindResource("MutedTextBrush") : (System.Windows.Media.Brush)FindResource("DangerBrush"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
            layout.Children.Add(details);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var autostart = new CheckBox { Content = "Autostart", IsChecked = plugin.Autostart, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            autostart.Checked += (_, _) => Runtime.SetAutostart(plugin.Manifest.Id, true);
            autostart.Unchecked += (_, _) => Runtime.SetAutostart(plugin.Manifest.Id, false);
            actions.Children.Add(autostart);
            var launch = new Button { Content = plugin.IsRunning ? "Stop" : "Launch", Padding = new Thickness(14, 5, 14, 5) };
            launch.Click += async (_, _) => await RunSafeAsync(async () => { if (plugin.IsRunning) await Runtime.StopAsync(plugin.Manifest.Id); else await Runtime.LaunchAsync(plugin.Manifest.Id); });
            actions.Children.Add(launch);
            var permissions = new Button { Content = "Permissions", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            permissions.Click += (_, _) => EditPermissions(plugin);
            actions.Children.Add(permissions);
            var catalogEntry = PluginRuntime.OfficialCatalog.FirstOrDefault(entry => string.Equals(entry.Id, plugin.Manifest.Id, StringComparison.Ordinal));
            if (catalogEntry is not null)
            {
                var update = new Button { Content = "Update", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
                update.Click += async (_, _) => await InstallAsync(catalogEntry.InstallUrl);
                actions.Children.Add(update);
            }
            var rollback = new Button { Content = "Rollback", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            rollback.Click += async (_, _) => await RunSafeAsync(async () =>
            {
                if (!await Runtime.RollbackAsync(plugin.Manifest.Id))
                    throw new InvalidOperationException("No previous verified version is available for rollback.");
            });
            actions.Children.Add(rollback);
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove {plugin.Manifest.Name}? Its plugin data is retained.", "Remove plugin", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    await RunSafeAsync(() => Runtime.RemoveAsync(plugin.Manifest.Id));
            };
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            layout.Children.Add(actions);
            card.Child = layout;
            InstalledList.Items.Add(card);
        }

        AvailableList.Items.Clear();
        var installedIds = Runtime.Installed.Select(plugin => plugin.Manifest.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in PluginRuntime.OfficialCatalog.Where(entry => !installedIds.Contains(entry.Id)))
        {
            var row = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("ElevatedBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new StackPanel
            {
                Children = { new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold }, new TextBlock { Text = entry.Description, Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 3, 8, 0) } }
            });
            var install = new Button { Content = "Install", Padding = new Thickness(14, 5, 14, 5) };
            install.Click += async (_, _) => { InstallUrlBox.Text = entry.InstallUrl; await InstallAsync(entry.InstallUrl); };
            Grid.SetColumn(install, 1);
            grid.Children.Add(install);
            row.Child = grid;
            AvailableList.Items.Add(row);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync(InstallUrlBox.Text.Trim());

    private async Task InstallAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var official = Runtime.IsOfficialUrl(url);
            var allowUnsigned = false;
            if (!official)
            {
                if (MessageBox.Show(this,
                    "This is an advanced sideload. The package is not in the official catalog and may be unsigned. It runs outside the launcher process but still has the permissions you grant. Continue?",
                    "Unsigned sideload warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                allowUnsigned = true;
            }
            var installed = await Runtime.InstallAsync(url, allowUnsigned);
            var consent = new PluginConsentWindow(installed.Manifest, installed.GrantedCapabilities) { Owner = this };
            if (consent.ShowDialog() == true) Runtime.SetCapabilities(installed.Manifest.Id, consent.SelectedCapabilities);
            MessageBox.Show(this, $"{installed.Manifest.Name} was installed. It can only use the permissions you selected.", "Plugin installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Plugin install failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditPermissions(InstalledPlugin plugin)
    {
        var consent = new PluginConsentWindow(plugin.Manifest, plugin.GrantedCapabilities) { Owner = this };
        if (consent.ShowDialog() == true) Runtime.SetCapabilities(plugin.Manifest.Id, consent.SelectedCapabilities);
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Plugin operation failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        Runtime.Changed -= Runtime_Changed;
        base.OnClosed(e);
    }
}

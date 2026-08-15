using System.Windows;
using System.Windows.Controls;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class SettingsDialog : Window
{
    private readonly LauncherSettings _settings;

    public bool ClearToolsRequested => ClearToolsBox.IsChecked == true;
    public bool ClearSessionsRequested => ClearSessionsBox.IsChecked == true;

    public SettingsDialog(LauncherSettings settings)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        _settings = settings;
        UpdateChecksBox.IsChecked = settings.UpdateChecksEnabled;
        ContinueOnFailureBox.IsChecked = settings.ContinueOnFailure;
        RememberSelectionsBox.IsChecked = settings.RememberSelections;
        SelectByTag(TimeoutBox, settings.LaunchTimeoutSeconds.ToString());
        SelectByTag(DelayBox, settings.LaunchDelaySeconds.ToString());
        SelectByTag(LauncherBox, settings.PreferredLauncher);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ClearSessionsRequested)
        {
            var answer = MessageBox.Show(
                this,
                "This will sign every saved profile out of Roblox after the next restart. Continue?",
                "Clear all browser sessions",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _settings.UpdateChecksEnabled = UpdateChecksBox.IsChecked == true;
        _settings.ContinueOnFailure = ContinueOnFailureBox.IsChecked == true;
        _settings.RememberSelections = RememberSelectionsBox.IsChecked == true;
        _settings.LaunchTimeoutSeconds = ReadIntTag(TimeoutBox, 45);
        _settings.LaunchDelaySeconds = ReadIntTag(DelayBox, 0);
        _settings.PreferredLauncher = ReadTag(LauncherBox, "Auto");
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items[0];
    }

    private static string ReadTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static int ReadIntTag(ComboBox comboBox, int fallback) =>
        int.TryParse(ReadTag(comboBox, fallback.ToString()), out var value) ? value : fallback;
}

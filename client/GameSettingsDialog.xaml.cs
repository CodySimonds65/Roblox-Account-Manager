using System.Windows;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class GameSettingsDialog : Window
{
    public GameSettings? Settings { get; private set; }

    public GameSettingsDialog(string gameName, GameSettings? settings)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        Heading.Text = $"{gameName} settings";
        Subtitle.Text = "Override the global engine settings for this saved game. Leave values inherited to use the global defaults.";
        Loaded += (_, _) => Editor.LoadSettings(settings);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Editor.TryReadSettings(out var settings, out var error))
        {
            MessageBox.Show(this, error, "Game settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = settings.HasOverrides ? settings : null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

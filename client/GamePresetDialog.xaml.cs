using System.Windows;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class GamePresetDialog : Window
{
    public string GameName => GameNameBox.Text.Trim();
    public string GameUrl { get; private set; } = string.Empty;

    public GamePresetDialog()
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        Loaded += (_, _) => GameNameBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameName))
        {
            MessageBox.Show(this, "Enter a recognizable game name.", "Game name");
            GameNameBox.Focus();
            return;
        }

        if (!GamePreset.TryNormalizeRobloxGameUrl(GameUrlBox.Text, out var normalizedUrl))
        {
            MessageBox.Show(this, "Enter a valid Roblox game URL, such as https://www.roblox.com/games/123456/Game-Name.", "Game URL");
            GameUrlBox.Focus();
            GameUrlBox.SelectAll();
            return;
        }

        GameUrl = normalizedUrl;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

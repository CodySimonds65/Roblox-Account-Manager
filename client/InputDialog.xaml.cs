using System.Windows;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class InputDialog : Window
{
    public string AccountLabel => LabelBox.Text.Trim();
    public string AccountGroup => GroupBox.Text.Trim();
    public bool EmbedInClients => EmbedBox.IsChecked == true;

    public InputDialog(string? label = null, string? group = null, bool? embedInClients = null)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        LabelBox.Text = label ?? string.Empty;
        GroupBox.Text = group ?? string.Empty;
        if (embedInClients is null)
        {
            EmbedBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmbedBox.IsChecked = embedInClients.Value;
        }

        Title = label is null ? "Add account profile" : "Edit account profile";
        Loaded += (_, _) => LabelBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AccountLabel))
        {
            MessageBox.Show(this, "Enter a recognizable label for this account.", "Account label");
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

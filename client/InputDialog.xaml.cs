using System.Windows;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class InputDialog : Window
{
    public string AccountLabel => LabelBox.Text.Trim();
    public string AccountGroup => GroupBox.Text.Trim();
    public bool IsFavorite => FavoriteBox.IsChecked == true;

    public InputDialog(string? label = null, string? group = null, bool isFavorite = false)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        LabelBox.Text = label ?? string.Empty;
        GroupBox.Text = group ?? string.Empty;
        FavoriteBox.IsChecked = isFavorite;
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

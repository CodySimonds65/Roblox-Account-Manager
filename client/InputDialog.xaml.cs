using System.Windows;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class InputDialog : Window
{
    public string AccountLabel => LabelBox.Text.Trim();

    public InputDialog()
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
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

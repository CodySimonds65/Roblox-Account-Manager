using System.Windows;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class CompatibilityDialog : Window
{
    private readonly IReadOnlyList<CompatibilityCheck> _checks;

    public CompatibilityDialog(IReadOnlyList<CompatibilityCheck> checks)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        _checks = checks;
        ChecksList.ItemsSource = checks;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CompatibilityService.CreateSafeReport(_checks));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

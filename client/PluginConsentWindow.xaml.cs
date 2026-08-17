using System.Windows;
using System.Windows.Controls;
using RobloxAltClient.Plugins;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class PluginConsentWindow : Window
{
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.Ordinal);

    public PluginConsentWindow(PluginManifest manifest, IReadOnlySet<string> granted)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);
        TitleText.Text = $"Permissions · {manifest.Name}";
        foreach (var capability in manifest.Capabilities.OrderBy(value => value, StringComparer.Ordinal))
        {
            var check = new CheckBox
            {
                Content = CapabilityDescription(capability),
                Tag = capability,
                IsChecked = granted.Contains(capability),
                Margin = new Thickness(0, 0, 0, 10),
                ToolTip = capability
            };
            _checks[capability] = check;
            CapabilityList.Children.Add(check);
        }
    }

    public IReadOnlySet<string> SelectedCapabilities =>
        _checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static string CapabilityDescription(string capability) => capability switch
    {
        PluginCapabilities.HostInputBackground => "Send background key and mouse messages to managed Roblox windows",
        PluginCapabilities.SystemWatchGlobalInput => "Observe global input while recording",
        PluginCapabilities.SystemReadScreen => "Capture screen regions for OCR/color matching",
        PluginCapabilities.HostActionsRegister => "Register actions for other plugins",
        PluginCapabilities.HostActionsInvoke => "Invoke actions exposed by other plugins",
        _ => capability
    };

    private void Save_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}

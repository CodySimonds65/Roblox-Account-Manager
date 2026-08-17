using System.Windows;
using System.Windows.Controls;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class GameSettingsEditor : UserControl
{
    private bool _loading;
    private bool _overrideMode;

    public bool IsOverrideMode
    {
        get => _overrideMode;
        set
        {
            _overrideMode = value;
            ConfigureOverrideLabels();
        }
    }

    public GameSettingsEditor()
    {
        InitializeComponent();
        Loaded += (_, _) => ConfigureOverrideLabels();
        SelectByTag(MsaaBox, string.Empty);
        SelectByTag(GraphicsQualityBox, string.Empty);
        SelectByTag(TextureQualityBox, string.Empty);
        SelectByTag(FpsBox, string.Empty);
        SelectByTag(VolumeBox, string.Empty);
        SelectByTag(ClientScalingBox, "Auto");
        CustomFpsBox.IsEnabled = false;
    }

    public void LoadSettings(GameSettings? settings)
    {
        _loading = true;
        try
        {
            var value = settings ?? new GameSettings();
            SelectByTag(MsaaBox, value.MsaaSamples?.ToString() ?? string.Empty);
            SelectByTag(GraphicsQualityBox, value.GraphicsQuality?.ToString() ?? string.Empty);
            SelectByTag(TextureQualityBox, value.TextureQuality?.ToString() ?? string.Empty);
            if (value.FpsLimit is null)
            {
                SelectByTag(FpsBox, string.Empty);
                CustomFpsBox.Text = string.Empty;
            }
            else if (FpsBox.Items.Cast<ComboBoxItem>().Any(item => item.Tag?.ToString() == value.FpsLimit.ToString()))
            {
                SelectByTag(FpsBox, value.FpsLimit.ToString()!);
                CustomFpsBox.Text = string.Empty;
            }
            else
            {
                SelectByTag(FpsBox, "Custom");
                CustomFpsBox.Text = value.FpsLimit.ToString();
            }

            SelectByTag(VolumeBox, value.MasterVolumeLevel?.ToString() ?? string.Empty);

            if (IsOverrideMode)
            {
                ClientScalingBox.Items.Clear();
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Inherit lower level", Tag = "Inherit" });
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Disabled", Tag = "Disabled" });
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Preserve quality", Tag = "Preserve" });
                SelectByTag(ClientScalingBox, value.PreserveRenderingQuality switch
                {
                    true => "Preserve",
                    false => "Disabled",
                    _ => "Inherit"
                });
            }
            else
            {
                SelectByTag(ClientScalingBox, value.PreserveRenderingQuality == true ? "Preserve" : "Auto");
            }

            PreserveQualityBox.IsChecked = value.PreserveRenderingQuality == true;
            AdvancedFlagsBox.Text = value.AdvancedFlagsJson ?? string.Empty;
            ValidationText.Text = string.Empty;
        }
        finally
        {
            _loading = false;
            UpdateControlState();
        }
    }

    public bool TryReadSettings(out GameSettings settings, out string error)
    {
        settings = new GameSettings();
        error = string.Empty;

        if (!TryReadIntTag(MsaaBox, out var msaa, allowEmpty: true, minimum: 0, maximum: 8, out error) ||
            !TryReadIntTag(GraphicsQualityBox, out var graphics, allowEmpty: true, minimum: 1, maximum: 10, out error) ||
            !TryReadIntTag(TextureQualityBox, out var texture, allowEmpty: true, minimum: 0, maximum: 6, out error) ||
            !TryReadIntTag(VolumeBox, out var volume, allowEmpty: true, minimum: 0, maximum: 10, out error))
        {
            ValidationText.Text = error;
            return false;
        }

        int? fps = null;
        var fpsTag = ReadTag(FpsBox);
        if (string.Equals(fpsTag, "Custom", StringComparison.Ordinal))
        {
            if (!int.TryParse(CustomFpsBox.Text.Trim(), out var customFps) || customFps is < 30 or > 1000)
            {
                error = "Custom FPS must be a whole number between 30 and 1000.";
                ValidationText.Text = error;
                return false;
            }

            fps = customFps;
        }
        else if (!string.IsNullOrEmpty(fpsTag))
        {
            fps = int.Parse(fpsTag);
        }

        if (!RobloxClientSettingsService.TryParseAdvancedFlags(AdvancedFlagsBox.Text, out _, out error))
        {
            ValidationText.Text = error;
            return false;
        }

        var scalingTag = ReadTag(ClientScalingBox);
        settings = new GameSettings
        {
            MsaaSamples = msaa,
            GraphicsQuality = graphics,
            TextureQuality = texture,
            FpsLimit = fps,
            MasterVolumeLevel = volume,
            PreserveRenderingQuality = scalingTag switch
            {
                "Preserve" => true,
                "Disabled" => false,
                _ => null
            },
            AdvancedFlagsJson = string.IsNullOrWhiteSpace(AdvancedFlagsBox.Text)
                ? null
                : RobloxClientSettingsService.FormatAdvancedFlags(AdvancedFlagsBox.Text)
        };

        ValidationText.Text = string.Empty;
        return true;
    }

    private void ConfigureOverrideLabels()
    {
        if (!IsLoaded || _loading)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            var inheritedLabel = IsOverrideMode ? "Inherit lower level" : "Automatic";
            SetFirstItemLabel(MsaaBox, inheritedLabel);
            SetFirstItemLabel(GraphicsQualityBox, inheritedLabel);
            SetFirstItemLabel(TextureQualityBox, inheritedLabel);
            SetFirstItemLabel(FpsBox, inheritedLabel);
            SetFirstItemLabel(VolumeBox, inheritedLabel);
            TipText.Text = IsOverrideMode
                ? "Tip: Inherit lower level leaves this scope unchanged. Curated controls take priority over duplicate advanced flags."
                : "Tip: Automatic removes only that override. Curated controls take priority over duplicate advanced flags.";

            var current = ReadTag(ClientScalingBox);
            ClientScalingBox.Items.Clear();
            if (IsOverrideMode)
            {
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Inherit lower level", Tag = "Inherit" });
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Disabled", Tag = "Disabled" });
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Preserve quality", Tag = "Preserve" });
                SelectByTag(ClientScalingBox, current is "Preserve" or "Disabled" ? current : "Inherit");
            }
            else
            {
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "Auto" });
                ClientScalingBox.Items.Add(new ComboBoxItem { Content = "Preserve quality", Tag = "Preserve" });
                SelectByTag(ClientScalingBox, current == "Preserve" ? "Preserve" : "Auto");
            }
        }
        finally
        {
            _loading = wasLoading;
            UpdateControlState();
        }
    }

    private void PreserveQuality_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            SelectByTag(ClientScalingBox, "Preserve");
        }
    }

    private void PreserveQuality_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_loading && !string.Equals(ReadTag(ClientScalingBox), "Inherit", StringComparison.Ordinal))
        {
            SelectByTag(ClientScalingBox, IsOverrideMode ? "Disabled" : "Auto");
        }
    }

    private void ClientScalingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var tag = ReadTag(ClientScalingBox);
        PreserveQualityBox.IsChecked = tag == "Preserve";
    }

    private void FpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControlState();

    private void UpdateControlState() => CustomFpsBox.IsEnabled = string.Equals(ReadTag(FpsBox), "Custom", StringComparison.Ordinal);

    private void FormatFlags_Click(object sender, RoutedEventArgs e)
    {
        if (!RobloxClientSettingsService.TryParseAdvancedFlags(AdvancedFlagsBox.Text, out _, out var error))
        {
            ValidationText.Text = error;
            return;
        }

        AdvancedFlagsBox.Text = RobloxClientSettingsService.FormatAdvancedFlags(AdvancedFlagsBox.Text);
        ValidationText.Text = string.Empty;
    }

    private void ResetFlags_Click(object sender, RoutedEventArgs e)
    {
        AdvancedFlagsBox.Clear();
        ValidationText.Text = string.Empty;
    }

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        var selectedItem = comboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (selectedItem is not null)
        {
            comboBox.SelectedItem = selectedItem;
        }
        else if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static string ReadTag(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static void SetFirstItemLabel(ComboBox comboBox, string label)
    {
        if (comboBox.Items.Count > 0 && comboBox.Items[0] is ComboBoxItem firstItem)
        {
            firstItem.Content = label;
        }
    }

    private static bool TryReadIntTag(
        ComboBox comboBox,
        out int? value,
        bool allowEmpty,
        int minimum,
        int maximum,
        out string error)
    {
        var tag = ReadTag(comboBox);
        if (string.IsNullOrEmpty(tag) && allowEmpty)
        {
            value = null;
            error = string.Empty;
            return true;
        }

        if (!int.TryParse(tag, out var parsed) || parsed < minimum || parsed > maximum)
        {
            value = null;
            error = $"The selected value must be between {minimum} and {maximum}.";
            return false;
        }

        value = parsed;
        error = string.Empty;
        return true;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Desktop.ViewModels;
using RobloxAccountManager.Desktop.Views;
using RobloxAccountManager.Platform.MacOS;

namespace RobloxAccountManager.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.Parse("#7C5CFC"))),
                new Setter(Button.ForegroundProperty, Brushes.White),
                new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.Parse("#7C5CFC"))),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.PaddingProperty, new Thickness(15, 9)),
                new Setter(Button.FontSizeProperty, 13d),
                new Setter(Button.FontWeightProperty, FontWeight.SemiBold)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TextBox.BackgroundProperty, new SolidColorBrush(Color.Parse("#0D1016"))),
                new Setter(TextBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#272D3A"))),
                new Setter(TextBox.BorderThicknessProperty, new Thickness(1)),
                new Setter(TextBox.PaddingProperty, new Thickness(11, 8)),
                new Setter(TextBox.FontSizeProperty, 13d)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(ComboBox.BackgroundProperty, new SolidColorBrush(Color.Parse("#0D1016"))),
                new Setter(ComboBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#F5F7FA"))),
                new Setter(ComboBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#272D3A"))),
                new Setter(ComboBox.PaddingProperty, new Thickness(11, 8)),
                new Setter(ComboBox.FontSizeProperty, 13d)
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = OperatingSystem.IsMacOS() ? RobloxPlatform.MacOS :
                OperatingSystem.IsWindows() ? RobloxPlatform.Windows : RobloxPlatform.Unknown;
            var composition = DesktopComposition.Create(
                platform,
                TrustedRobloxIdentityConfiguration.LoadTeamIdentifier(),
                TrustedRobloxIdentityConfiguration.LoadInstallerIdentity());
            var shell = new DesktopShellViewModel(composition.Capabilities, composition.Accounts, composition.Presets, composition.Settings, composition.Updates, composition.RobloxSettings, composition.Plugins);
            if (composition.Plugins is MacPluginHostFacade macPlugins)
            {
                macPlugins.SetAccountSnapshotProvider(() => shell.Accounts.Select(account => new PluginAccountSnapshot(account.Id, account.Label, RobloxPlatform.MacOS)).ToArray());
            }
            desktop.MainWindow = new MainWindow(
                shell,
                composition.BrowserSessions,
                composition.Launches,
                composition.Clients);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

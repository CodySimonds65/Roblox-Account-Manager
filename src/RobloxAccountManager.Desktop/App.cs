using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Desktop.ViewModels;
using RobloxAccountManager.Desktop.Views;

namespace RobloxAccountManager.Desktop;

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

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
            desktop.MainWindow = new MainWindow(
                new DesktopShellViewModel(composition.Capabilities, composition.Updates),
                composition.BrowserSessions,
                composition.Launches,
                composition.Clients);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

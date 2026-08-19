using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Desktop.ViewModels;
using RobloxAccountManager.Desktop.Views;
using RobloxAccountManager.Platform.MacOS;

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

using Avalonia;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Desktop.Services;

namespace RobloxAccountManager.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--validate-composition", StringComparer.Ordinal))
        {
            var app = new App();
            app.Initialize();
            var platform = OperatingSystem.IsMacOS() ? RobloxPlatform.MacOS :
                OperatingSystem.IsWindows() ? RobloxPlatform.Windows : RobloxPlatform.Unknown;
            var composition = DesktopComposition.Create(
                platform,
                TrustedRobloxIdentityConfiguration.LoadInstallerIdentity());
            if (platform == RobloxPlatform.MacOS && composition.Clients is null)
                throw new InvalidOperationException("The macOS client services were not composed.");
            if (platform == RobloxPlatform.MacOS && composition.Launches is null)
                throw new InvalidOperationException("The macOS launch services were not composed.");
            var browserSessions = composition.BrowserSessions;
            var accountId = Guid.NewGuid().ToString("N");
            var descriptor = browserSessions.CreateAsync(accountId, "Composition test").AsTask().GetAwaiter().GetResult();
            if (descriptor.DataStoreIdentifier != Guid.Parse(accountId).ToString("D"))
                throw new InvalidOperationException("The account browser data-store identity was not stable.");
            browserSessions.DisposeAsync(accountId).AsTask().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

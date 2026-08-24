using Avalonia;
using System.Text.Json;
using RobloxAccountManager.Core.Contracts;
using RobloxAccountManager.Core.Data;
using RobloxAccountManager.Core.Models;
using RobloxAccountManager.Desktop.Services;

namespace RobloxAccountManager.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine(
                "desktop-platform-not-supported: the Avalonia desktop is macOS-only; use the WPF client under client/ on Windows.");
            Environment.ExitCode = 2;
            return;
        }

        var validationMode = DesktopStartupPlan.ParseValidationMode(args);
        if (args.Contains("--validate-composition", StringComparer.Ordinal))
        {
            var app = new App();
            app.Initialize();
            var composition = DesktopComposition.Create(
                RobloxPlatform.MacOS,
                TrustedRobloxIdentityConfiguration.LoadInstallerIdentity());
            if (composition.Clients is null)
                throw new InvalidOperationException("The macOS client services were not composed.");
            if (composition.Launches is null)
                throw new InvalidOperationException("The macOS launch services were not composed.");
            var browserSessions = composition.BrowserSessions;
            var accountId = Guid.NewGuid().ToString("N");
            var descriptor = browserSessions.CreateAsync(accountId, "Composition test").AsTask().GetAwaiter().GetResult();
            if (descriptor.DataStoreIdentifier != Guid.Parse(accountId).ToString("D"))
                throw new InvalidOperationException("The account browser data-store identity was not stable.");
            browserSessions.DisposeAsync(accountId).AsTask().GetAwaiter().GetResult();
            return;
        }

        if (validationMode == DesktopValidationMode.None)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        var validationRoot = Path.Combine(
            Path.GetTempPath(),
            "RobloxAccountManager-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationRoot);
        try
        {
            if (validationMode == DesktopValidationMode.BrowserStartup)
            {
                var paths = new LauncherDataPaths(validationRoot);
                Directory.CreateDirectory(paths.Root);
                var account = new AccountProfile { Label = "Startup validation account", SortOrder = 0 };
                File.WriteAllText(
                    paths.Accounts,
                    JsonSerializer.Serialize(new[] { account }, new JsonSerializerOptions { WriteIndented = true }));
            }

            BuildAvaloniaApp(validationMode, validationRoot)
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        finally
        {
            try
            {
                if (Directory.Exists(validationRoot)) Directory.Delete(validationRoot, recursive: true);
            }
            catch
            {
                // Native WebKit may still be releasing its temporary store when the process exits.
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp(
        DesktopValidationMode validationMode = DesktopValidationMode.None,
        string? dataRoot = null)
    {
        App.ConfigureStartup(validationMode, dataRoot);
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}

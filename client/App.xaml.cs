using System.Windows;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (UpdateService.IsApplyUpdateMode(e.Args))
        {
            try
            {
                UpdateService.ApplyUpdate(e.Args);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "Roblox Alt Client update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\RobloxAltClient", out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(
                "Roblox Alt Client is already open.",
                "Already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}

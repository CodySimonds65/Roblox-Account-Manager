using System.Windows;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class App : Application
{
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

        base.OnStartup(e);
    }
}

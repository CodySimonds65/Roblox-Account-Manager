using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RobloxAltClient.Services;

public static class WindowAppearance
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int RoundCorners = 2;

    public static void ApplyModernChrome(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return;
            }

            var handle = new WindowInteropHelper(window).Handle;
            var darkMode = 1;
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref darkMode, sizeof(int));

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var cornerPreference = RoundCorners;
                _ = DwmSetWindowAttribute(handle, WindowCornerPreference, ref cornerPreference, sizeof(int));
            }
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}

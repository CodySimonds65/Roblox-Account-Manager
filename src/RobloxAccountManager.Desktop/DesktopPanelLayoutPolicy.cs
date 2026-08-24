namespace RobloxAccountManager.Desktop;

public static class DesktopPanelLayoutPolicy
{
    public const double ContentMinimumHeight = 460;
    public const double ActivityMinimumHeight = 210;
    public const double BrowserMinimumHeight = 220;
    public const double FixedChromeMinimumHeight = 150;
    public const double RequiredWindowHeight = ContentMinimumHeight + ActivityMinimumHeight + FixedChromeMinimumHeight;
    public const double WindowMinimumHeight = 820;

    public static bool CanRenderWithoutClipping(double contentHeight, double activityHeight) =>
        contentHeight >= ContentMinimumHeight && activityHeight >= ActivityMinimumHeight;
}

namespace RobloxAccountManager.Desktop;

public static class DesktopPanelLayoutPolicy
{
    public const double ContentMinimumHeight = 460;
    public const double ActivityMinimumHeight = 150;
    public const double BrowserMinimumHeight = 220;
    public const double FixedChromeMinimumHeight = 150;
    public const double RequiredWindowHeight = ContentMinimumHeight + ActivityMinimumHeight + FixedChromeMinimumHeight;
    public const double WindowMinimumHeight = 760;

    public static bool CanRenderWithoutClipping(double contentHeight, double activityHeight) =>
        contentHeight >= ContentMinimumHeight && activityHeight >= ActivityMinimumHeight;

    public static double GetMaximumActivityHeight(double availableHeight, double fixedChromeHeight)
    {
        if (!double.IsFinite(availableHeight) || !double.IsFinite(fixedChromeHeight))
            return ActivityMinimumHeight;

        return Math.Max(
            ActivityMinimumHeight,
            availableHeight - fixedChromeHeight - ContentMinimumHeight);
    }

    public static bool UseCompactPresetBar(double availableWidth) =>
        double.IsFinite(availableWidth) && availableWidth >= 1200;
}

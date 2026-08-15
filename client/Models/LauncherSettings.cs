namespace RobloxAltClient.Models;

public sealed class LauncherSettings
{
    public bool UpdateChecksEnabled { get; set; } = true;
    public int LaunchTimeoutSeconds { get; set; } = 45;
    public int LaunchDelaySeconds { get; set; }
    public bool ContinueOnFailure { get; set; } = true;
    public bool RememberSelections { get; set; } = true;
    public string PreferredLauncher { get; set; } = "Auto";
    public List<string> LastSelectedProfileIds { get; set; } = [];
    public string LastGameName { get; set; } = string.Empty;
    public List<string> RecentGameNames { get; set; } = [];
    public bool ClearBrowserDataOnNextStart { get; set; }
}

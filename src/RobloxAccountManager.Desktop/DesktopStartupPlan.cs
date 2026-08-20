using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Desktop;

public enum DesktopValidationMode
{
    None,
    GuiStartup,
    BrowserStartup
}

public sealed record DesktopStartupPlan(AccountProfile? InitialAccount, bool ActivateBrowserOnStartup)
{
    public static IReadOnlyList<AccountProfile> RestoreSelectedAccounts(
        IReadOnlyList<AccountProfile> accounts,
        LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.RememberSelections && settings.LastSelectedProfileIds.Count > 0)
        {
            var rememberedIds = settings.LastSelectedProfileIds.ToHashSet(StringComparer.Ordinal);
            var remembered = accounts.Where(account => rememberedIds.Contains(account.Id)).ToArray();
            if (remembered.Length > 0)
            {
                return remembered;
            }
        }

        return accounts.Take(1).ToArray();
    }

    public static DesktopStartupPlan Create(
        IReadOnlyList<AccountProfile> accounts,
        DesktopValidationMode validationMode)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var initialAccount = accounts.FirstOrDefault();
        return new(initialAccount, validationMode == DesktopValidationMode.BrowserStartup && initialAccount is not null);
    }

    public static DesktopValidationMode ParseValidationMode(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = args.ToArray();
        var gui = values.Contains("--validate-gui-startup", StringComparer.Ordinal);
        var browser = values.Contains("--validate-browser-startup", StringComparer.Ordinal);
        if (gui && browser)
            throw new ArgumentException("GUI and browser startup validation modes cannot be combined.", nameof(args));
        return browser ? DesktopValidationMode.BrowserStartup : gui ? DesktopValidationMode.GuiStartup : DesktopValidationMode.None;
    }
}

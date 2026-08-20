using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RobloxAccountManager.Core.Models;

namespace RobloxAccountManager.Desktop;

internal static class AccountRailTemplatePolicy
{
    public static FuncDataTemplate<AccountProfile> CreateTemplate(Func<AccountProfile, Control> buildRow)
    {
        ArgumentNullException.ThrowIfNull(buildRow);
        return new FuncDataTemplate<AccountProfile>((account, _) => Build(account, buildRow));
    }

    public static Control Build(AccountProfile? account, Func<AccountProfile, Control> buildRow)
    {
        ArgumentNullException.ThrowIfNull(buildRow);
        return account is null ? new Border() : buildRow(account);
    }
}

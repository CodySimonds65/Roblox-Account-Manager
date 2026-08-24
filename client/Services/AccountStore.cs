namespace RobloxAltClient.Services;

public sealed class AccountStore
{
    private readonly RobloxAccountManager.Core.Data.AccountStore _inner;

    public string AppDataDirectory => _inner.AppDataDirectory;

    public string WebViewDataDirectory => _inner.WebViewDataDirectory;

    public AccountStore(string? appDataDirectory = null)
    {
        _inner = appDataDirectory is null
            ? new RobloxAccountManager.Core.Data.AccountStore()
            : new RobloxAccountManager.Core.Data.AccountStore(appDataDirectory);
    }

    public Task<List<AccountProfile>> LoadAsync() => _inner.LoadAsync();

    public static List<AccountProfile> OrderForDisplay(IEnumerable<AccountProfile> accounts) =>
        RobloxAccountManager.Core.Data.AccountStore.OrderForDisplay(accounts);

    public Task SaveAsync(IEnumerable<AccountProfile> accounts) => _inner.SaveAsync(accounts);
}

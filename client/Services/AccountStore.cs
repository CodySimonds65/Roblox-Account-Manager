using System.Text.Json;
using System.IO;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string AppDataDirectory { get; }

    public string WebViewDataDirectory => Path.Combine(AppDataDirectory, "WebView2");
    private string AccountFile => Path.Combine(AppDataDirectory, "accounts.json");

    public AccountStore(string? appDataDirectory = null)
    {
        AppDataDirectory = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient");
    }

    public async Task<List<AccountProfile>> LoadAsync()
    {
        Directory.CreateDirectory(AppDataDirectory);
        return await JsonFileStore.LoadAsync(AccountFile, new List<AccountProfile>(), JsonOptions);
    }

    public async Task SaveAsync(IEnumerable<AccountProfile> accounts)
    {
        await JsonFileStore.SaveAsync(AccountFile, accounts.ToList(), JsonOptions);
    }
}

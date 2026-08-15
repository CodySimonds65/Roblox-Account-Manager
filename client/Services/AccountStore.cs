using System.Text.Json;
using System.IO;
using RobloxAltClient.Models;

namespace RobloxAltClient.Services;

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RobloxAltClient");

    public string WebViewDataDirectory => Path.Combine(AppDataDirectory, "WebView2");
    private string AccountFile => Path.Combine(AppDataDirectory, "accounts.json");

    public async Task<List<AccountProfile>> LoadAsync()
    {
        Directory.CreateDirectory(AppDataDirectory);
        if (!File.Exists(AccountFile))
        {
            return [];
        }

        await using var stream = File.OpenRead(AccountFile);
        return await JsonSerializer.DeserializeAsync<List<AccountProfile>>(stream, JsonOptions) ?? [];
    }

    public async Task SaveAsync(IEnumerable<AccountProfile> accounts)
    {
        Directory.CreateDirectory(AppDataDirectory);
        await using var stream = File.Create(AccountFile);
        await JsonSerializer.SerializeAsync(stream, accounts, JsonOptions);
    }
}

namespace RobloxAltClient.Models;

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Roblox account";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Group { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public bool EmbedInClients { get; set; }
    public int SortOrder { get; set; }
    public GameSettings? GameSettings { get; set; }

    public override string ToString() => Label;
}

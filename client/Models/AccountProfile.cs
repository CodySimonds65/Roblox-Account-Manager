namespace RobloxAltClient.Models;

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Roblox account";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public override string ToString() => Label;
}

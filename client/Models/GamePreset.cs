namespace RobloxAltClient.Models;

public sealed record GamePreset(string Name, string Url)
{
    public override string ToString() => Name;
}

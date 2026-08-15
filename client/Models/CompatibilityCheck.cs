namespace RobloxAltClient.Models;

public enum CompatibilityCheckState
{
    Ready,
    Info,
    Warning
}

public sealed record CompatibilityCheck(
    string Name,
    CompatibilityCheckState State,
    string Summary,
    string Detail)
{
    public string StateLabel => State.ToString().ToUpperInvariant();
}

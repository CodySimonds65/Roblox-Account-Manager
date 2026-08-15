namespace RobloxAltClient.Models;

public sealed record GamePreset(string Name, string Url, bool IsBuiltIn = false)
{
    public override string ToString() => Name;

    public static bool TryNormalizeRobloxGameUrl(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !string.Equals(segments[0], "games", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(segments[1], out _))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }
}

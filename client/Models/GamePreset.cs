namespace RobloxAltClient.Models;

public sealed record GamePreset(string Name, string Url, bool IsBuiltIn = false)
{
    public GameSettings? Settings { get; set; }

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
        if (segments.Length == 1 &&
            string.Equals(segments[0], "share", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetShareParameter(uri.Query, "code", out var code) ||
                string.IsNullOrWhiteSpace(code) ||
                !TryGetShareParameter(uri.Query, "type", out var type) ||
                !string.Equals(type, "Server", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalizedUrl = CanonicalizeHost(uri).AbsoluteUri;
            return true;
        }

        if (segments.Length < 2 ||
            !string.Equals(segments[0], "games", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(segments[1], out _))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static Uri CanonicalizeHost(Uri uri) =>
        string.Equals(uri.Host, "roblox.com", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "www.roblox.com" }.Uri
            : uri;

    private static bool TryGetShareParameter(string query, string expectedKey, out string value)
    {
        value = string.Empty;
        var found = false;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(Uri.UnescapeDataString(key), expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            found = true;
            value = Uri.UnescapeDataString(separator >= 0 ? pair[(separator + 1)..] : string.Empty);
        }

        return found;
    }
}

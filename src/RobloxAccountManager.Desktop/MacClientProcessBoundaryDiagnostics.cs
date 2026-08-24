using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Desktop;

public static class MacClientProcessBoundaryDiagnostics
{
    public static string Describe(IReadOnlyList<RobloxWindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) return "none";

        return string.Join(",", windows
            .OrderBy(window => window.Process.Pid)
            .Select(window =>
                $"pid={window.Process.Pid}:exe={LeafName(window.Process.ExecutablePath)}"
                + $":bundle={LeafName(window.Process.BundlePath)}"));
    }

    private static string LeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "none";
        var trimmed = path.TrimEnd('/', '\\');
        var separator = trimmed.LastIndexOfAny(['/', '\\']);
        var leaf = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return Sanitise(leaf);
    }

    private static string Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var characters = value
            .Where(character => char.IsLetterOrDigit(character)
                || character is '.' or '_' or '-')
            .ToArray();
        return characters.Length == 0 ? "none" : new string(characters);
    }
}

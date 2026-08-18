using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Embeds running game client windows as children of a launcher-owned top-level
/// window so the launcher can focus and inject real input into them without
/// stealing desktop focus from other applications.
/// </summary>
public sealed class ClientEmbeddingService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, nint> _embedded = new(StringComparer.Ordinal);
    private nint _hostWindow;

    /// <summary>Returns the embedded root HWND for an account when it is visible and selected; null otherwise.</summary>
    public Func<string, nint?>? EmbeddedRootResolver { get; set; }

    /// <summary>Brings an embedded account to the foreground tab and focuses its window.</summary>
    public Action<string>? EmbeddedActivate { get; set; }

    private const long WsPopup = 0x80000000L;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const int GwlStyle = -16;
    private const int SwHide = 0;
    private const int SwShow = 5;

    public bool IsEmbedded(string accountId)
    {
        lock (_gate) return _embedded.ContainsKey(accountId);
    }

    public nint? RootFor(string accountId)
    {
        lock (_gate) return _embedded.TryGetValue(accountId, out var root) ? root : null;
    }

    public void SetHostWindow(nint hostWindow) => _hostWindow = hostWindow;

    public bool TryEmbed(string accountId, nint rootWindow)
    {
        if (rootWindow == nint.Zero || _hostWindow == nint.Zero) return false;
        lock (_gate)
        {
            if (_embedded.TryGetValue(accountId, out var existing) && existing == rootWindow) return true;
            _embedded[accountId] = rootWindow;
        }
        var style = GetWindowLongPtr(rootWindow, GwlStyle).ToInt64();
        style = (style & ~WsPopup) | WsChild | WsVisible;
        SetWindowLongPtr(rootWindow, GwlStyle, new nint(style));
        SetParent(rootWindow, _hostWindow);
        return true;
    }

    public bool TryUnembed(string accountId)
    {
        nint rootWindow;
        lock (_gate)
        {
            if (!_embedded.Remove(accountId, out rootWindow)) return false;
        }
        if (rootWindow != nint.Zero && IsWindow(rootWindow))
        {
            SetParent(rootWindow, nint.Zero);
            var style = GetWindowLongPtr(rootWindow, GwlStyle).ToInt64();
            style = (style & ~WsChild) | WsPopup;
            SetWindowLongPtr(rootWindow, GwlStyle, new nint(style));
        }
        return true;
    }

    public void UnembedAll()
    {
        string[] ids;
        lock (_gate) ids = _embedded.Keys.ToArray();
        foreach (var id in ids) TryUnembed(id);
    }

    public void Layout(nint hostClientAreaHandle, int stripHeight)
    {
        if (_hostWindow == nint.Zero || !GetClientRect(hostClientAreaHandle, out var rect)) return;
        nint[] roots;
        lock (_gate) roots = _embedded.Values.ToArray();
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top - stripHeight);
        foreach (var root in roots)
        {
            if (root == nint.Zero) continue;
            MoveWindow(root, 0, stripHeight, width, height, true);
        }
    }

    public void ShowOnly(string accountId)
    {
        lock (_gate)
        {
            foreach (var (id, root) in _embedded)
            {
                if (root == nint.Zero) continue;
                ShowWindow(root, string.Equals(id, accountId, StringComparison.Ordinal) ? SwShow : SwHide);
            }
        }
    }

    public void Focus(string accountId)
    {
        lock (_gate)
        {
            if (!_embedded.TryGetValue(accountId, out var root) || root == nint.Zero) return;
            ShowWindow(root, SwShow);
            SetFocus(root);
        }
    }

    public string[] EmbeddedAccountIds()
    {
        lock (_gate) return _embedded.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint newParent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}

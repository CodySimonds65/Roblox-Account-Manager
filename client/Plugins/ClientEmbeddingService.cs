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
    private string? _visibleAccountId;
    private nint _hostWindow;

    /// <summary>Returns the embedded root HWND for an account when it is visible and selected; null otherwise.</summary>
    public Func<string, nint?>? EmbeddedRootResolver { get; set; }

    /// <summary>Brings an embedded account to the foreground tab and focuses its window.</summary>
    public Action<string>? EmbeddedActivate { get; set; }

    /// <summary>Decides whether an account is eligible for embedding/tab display; accounts returning false are unembedded.</summary>
    public Func<string, bool>? EmbedFilter { get; set; }

    /// <summary>Raised when the embedding eligibility of accounts may have changed.</summary>
    public event Action? FilterChanged;

    public void NotifyFilterChanged() => FilterChanged?.Invoke();

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

    public string? VisibleAccountId
    {
        get { lock (_gate) return _visibleAccountId; }
    }

    public nint? RootFor(string accountId)
    {
        lock (_gate) return _embedded.TryGetValue(accountId, out var root) ? root : null;
    }

    public void SetHostWindow(nint hostWindow) => _hostWindow = hostWindow;

    public bool TryEmbed(string accountId, nint rootWindow)
    {
        if (rootWindow == nint.Zero || _hostWindow == nint.Zero || !IsWindow(_hostWindow)) return false;
        nint previousRoot = nint.Zero;
        lock (_gate)
        {
            if (_embedded.TryGetValue(accountId, out var existing))
            {
                if (existing == rootWindow) return true;
                previousRoot = existing;
            }
            _embedded[accountId] = rootWindow;
        }
        if (previousRoot != nint.Zero && IsWindow(previousRoot))
        {
            SetParent(previousRoot, nint.Zero);
            var previousStyle = GetWindowLongPtr(previousRoot, GwlStyle).ToInt64();
            SetWindowLongPtr(previousRoot, GwlStyle, new nint((previousStyle & ~WsChild) | WsPopup));
        }
        var style = GetWindowLongPtr(rootWindow, GwlStyle).ToInt64();
        // Visibility is controlled by ShowOnly/ShowWindow; the child must not
        // appear on the desktop before it is shown inside the host tab.
        style = (style & ~WsPopup & ~WsVisible) | WsChild;
        SetWindowLongPtr(rootWindow, GwlStyle, new nint(style));
        SetParent(rootWindow, _hostWindow);
        return true;
    }

    public void HideRootWindow(nint rootWindow)
    {
        if (rootWindow == nint.Zero || !IsWindow(rootWindow)) return;
        ShowWindow(rootWindow, SwHide);
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

    public void Layout(int hostLeft, int hostTop, int hostWidth, int hostHeight)
    {
        // Never resize an embedded client to a degenerate size: shrinking a live
        // D3D swapchain to a few pixels (e.g., while the host section is still
        // collapsing or not yet laid out) can crash the game. Skip until the host
        // area has a real size.
        if (_hostWindow == nint.Zero || hostWidth < 64 || hostHeight < 64) return;
        nint[] roots;
        lock (_gate) roots = _embedded.Values.ToArray();
        foreach (var root in roots)
        {
            if (root == nint.Zero) continue;
            MoveWindow(root, hostLeft, hostTop, hostWidth, hostHeight, true);
        }
    }

    public void ShowOnly(string accountId)
    {
        lock (_gate)
        {
            _visibleAccountId = accountId;
            foreach (var (id, root) in _embedded)
            {
                if (root == nint.Zero) continue;
                ShowWindow(root, string.Equals(id, accountId, StringComparison.Ordinal) ? SwShow : SwHide);
            }
        }
    }

    public void HideAll()
    {
        lock (_gate)
        {
            _visibleAccountId = null;
            foreach (var root in _embedded.Values)
            {
                if (root != nint.Zero) ShowWindow(root, SwHide);
            }
        }
    }

    public void HideRoot(string accountId)
    {
        lock (_gate)
        {
            if (_embedded.TryGetValue(accountId, out var root) && root != nint.Zero)
                ShowWindow(root, SwHide);
        }
    }

    public void Focus(string accountId)
    {
        nint root;
        lock (_gate)
        {
            if (!_embedded.TryGetValue(accountId, out root) || root == nint.Zero) return;
            ShowWindow(root, SwShow);
        }
        // SetFocus requires the target window's thread to be attached to ours.
        var ourThread = GetCurrentThreadId();
        GetWindowThreadProcessId(root, out var gameThread);
        var attached = gameThread != 0 && gameThread != ourThread &&
                       AttachThreadInput(ourThread, gameThread, true);
        try
        {
            SetFocus(root);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, gameThread, false);
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
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);
}

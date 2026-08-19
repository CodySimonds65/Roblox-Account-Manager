using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Parents managed Roblox windows beneath the dedicated native Clients host.
/// The native hierarchy lets Windows route normal user input directly to the
/// selected game while macros continue to use their separate safety brokers.
/// </summary>
public sealed class ClientEmbeddingService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EmbeddedWindow> _embedded = new(StringComparer.Ordinal);
    private string? _visibleAccountId;
    private nint _hostWindow;

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
    private const int SwShowNoActivate = 4;
    private const uint GaRoot = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public bool IsEmbedded(string accountId)
    {
        lock (_gate)
            return _embedded.TryGetValue(accountId, out var embedded) && IsCurrent(embedded, _hostWindow);
    }

    public string? VisibleAccountId
    {
        get { lock (_gate) return _visibleAccountId; }
    }

    public bool IsVisible(string accountId)
    {
        lock (_gate)
        {
            return string.Equals(_visibleAccountId, accountId, StringComparison.Ordinal) &&
                   _embedded.TryGetValue(accountId, out var embedded) &&
                   IsCurrent(embedded, _hostWindow) && IsWindowVisible(embedded.Root);
        }
    }

    public bool HostOwnsForeground()
    {
        nint hostWindow;
        lock (_gate) hostWindow = _hostWindow;
        if (hostWindow == nint.Zero || !IsWindow(hostWindow)) return false;
        var foregroundRoot = GetAncestor(hostWindow, GaRoot);
        return foregroundRoot != nint.Zero && GetForegroundWindow() == foregroundRoot;
    }

    public nint? RootFor(string accountId)
    {
        lock (_gate)
        {
            return _embedded.TryGetValue(accountId, out var embedded) && IsCurrent(embedded, _hostWindow)
                ? embedded.Root
                : null;
        }
    }

    public void SetHostWindow(nint hostWindow)
    {
        lock (_gate)
        {
            if (_hostWindow == hostWindow) return;
        }

        UnembedAll();
        lock (_gate) _hostWindow = hostWindow != nint.Zero && IsWindow(hostWindow) ? hostWindow : nint.Zero;
    }

    public void ReleaseHostWindow(nint hostWindow)
    {
        lock (_gate)
        {
            if (_hostWindow != hostWindow) return;
        }

        UnembedAll();
        lock (_gate)
        {
            if (_hostWindow == hostWindow) _hostWindow = nint.Zero;
        }
    }

    public bool TryEmbed(string accountId, nint rootWindow, int expectedProcessId)
    {
        if (rootWindow == nint.Zero || expectedProcessId <= 0 || !IsWindow(rootWindow)) return false;
        GetWindowThreadProcessId(rootWindow, out var actualProcessId);
        if (actualProcessId != (uint)expectedProcessId) return false;

        EmbeddedWindow? existing;
        nint hostWindow;
        lock (_gate)
        {
            hostWindow = _hostWindow;
            _embedded.TryGetValue(accountId, out existing);
            if (existing is not null && existing.Root == rootWindow && IsCurrent(existing, hostWindow)) return true;
        }
        if (hostWindow == nint.Zero || !IsWindow(hostWindow)) return false;
        if (existing is not null) TryUnembed(accountId);

        var originalStyle = GetWindowLongPtr(rootWindow, GwlStyle).ToInt64();
        var originalParent = (originalStyle & WsChild) != 0 ? GetParent(rootWindow) : nint.Zero;
        if (!GetWindowRect(rootWindow, out var originalBounds)) return false;
        var originalVisible = IsWindowVisible(rootWindow);
        var childStyle = (originalStyle & ~WsPopup) | WsChild | WsVisible;
        if (!TrySetStyle(rootWindow, childStyle)) return false;

        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(rootWindow, hostWindow);
        if (previousParent == nint.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            TrySetStyle(rootWindow, originalStyle);
            return false;
        }
        SetWindowPos(rootWindow, nint.Zero, 0, 0, 0, 0,
            SwpNoActivate | SwpNoZOrder | SwpNoMove | SwpNoSize | SwpFrameChanged);

        var embedded = new EmbeddedWindow(
            rootWindow,
            (uint)expectedProcessId,
            originalParent,
            originalStyle,
            originalBounds,
            originalVisible);
        lock (_gate)
        {
            if (_hostWindow != hostWindow || !IsCurrent(embedded, hostWindow))
            {
                RestoreWindow(embedded);
                return false;
            }
            _embedded[accountId] = embedded;
        }

        Layout();
        return true;
    }

    public bool TryUnembed(string accountId)
    {
        EmbeddedWindow embedded;
        lock (_gate)
        {
            if (!_embedded.Remove(accountId, out embedded!)) return false;
            if (string.Equals(_visibleAccountId, accountId, StringComparison.Ordinal)) _visibleAccountId = null;
        }
        RestoreWindow(embedded);
        return true;
    }

    public void UnembedAll()
    {
        string[] ids;
        lock (_gate) ids = _embedded.Keys.ToArray();
        foreach (var id in ids) TryUnembed(id);
    }

    public void Layout()
    {
        nint hostWindow;
        EmbeddedWindow[] embedded;
        lock (_gate)
        {
            hostWindow = _hostWindow;
            embedded = _embedded.Values.ToArray();
        }
        if (hostWindow == nint.Zero || !IsWindow(hostWindow) || !GetClientRect(hostWindow, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 64 || height < 64) return;

        foreach (var window in embedded)
        {
            if (!IsCurrent(window, hostWindow)) continue;
            SetWindowPos(window.Root, nint.Zero, 0, 0, width, height, SwpNoActivate | SwpNoZOrder);
        }
    }

    public void ShowOnly(string accountId)
    {
        lock (_gate)
        {
            _visibleAccountId = accountId;
            foreach (var (id, embedded) in _embedded)
            {
                if (!IsCurrent(embedded, _hostWindow)) continue;
                // Showing a selected client must not activate it. The caller
                // performs an explicit guarded focus handoff only when RAM
                // already owns foreground.
                ShowWindow(embedded.Root, string.Equals(id, accountId, StringComparison.Ordinal) ? SwShowNoActivate : SwHide);
            }
        }
    }

    public void HideAll()
    {
        lock (_gate)
        {
            _visibleAccountId = null;
            foreach (var embedded in _embedded.Values)
            {
                if (IsCurrent(embedded, _hostWindow)) ShowWindow(embedded.Root, SwHide);
            }
        }
    }

    public void HideRoot(string accountId)
    {
        lock (_gate)
        {
            if (_embedded.TryGetValue(accountId, out var embedded) && IsCurrent(embedded, _hostWindow))
                ShowWindow(embedded.Root, SwHide);
        }
    }

    public string[] EmbeddedAccountIds()
    {
        lock (_gate)
        {
            return _embedded.Where(pair => IsCurrent(pair.Value, _hostWindow))
                .Select(pair => pair.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static bool IsCurrent(EmbeddedWindow embedded, nint hostWindow)
    {
        if (hostWindow == nint.Zero || embedded.Root == nint.Zero || !IsWindow(embedded.Root)) return false;
        GetWindowThreadProcessId(embedded.Root, out var processId);
        return processId == embedded.ProcessId && GetParent(embedded.Root) == hostWindow;
    }

    private static bool TrySetStyle(nint window, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(window, GwlStyle, new nint(style));
        return previous != nint.Zero || Marshal.GetLastPInvokeError() == 0;
    }

    private static void RestoreWindow(EmbeddedWindow embedded)
    {
        if (embedded.Root == nint.Zero || !IsWindow(embedded.Root)) return;
        GetWindowThreadProcessId(embedded.Root, out var processId);
        if (processId != embedded.ProcessId) return;

        ShowWindow(embedded.Root, SwHide);
        SetParent(embedded.Root, embedded.OriginalParent);
        TrySetStyle(embedded.Root, embedded.OriginalStyle);
        var width = Math.Max(1, embedded.OriginalBounds.Right - embedded.OriginalBounds.Left);
        var height = Math.Max(1, embedded.OriginalBounds.Bottom - embedded.OriginalBounds.Top);
        SetWindowPos(
            embedded.Root,
            nint.Zero,
            embedded.OriginalBounds.Left,
            embedded.OriginalBounds.Top,
            width,
            height,
            SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
        ShowWindow(embedded.Root, embedded.OriginalVisible ? SwShowNoActivate : SwHide);
    }

    private sealed record EmbeddedWindow(
        nint Root,
        uint ProcessId,
        nint OriginalParent,
        long OriginalStyle,
        RECT OriginalBounds,
        bool OriginalVisible);

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint newParent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }
}

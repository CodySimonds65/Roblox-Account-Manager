using System.Runtime.InteropServices;
using System.Diagnostics;

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
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsBorder = 0x00800000L;
    private const long WsClipChildren = 0x02000000L;
    private const long WsClipSiblings = 0x04000000L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExWindowEdge = 0x00000100L;
    private const long WsExDlgModalFrame = 0x00000001L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExStaticEdge = 0x00020000L;
    private const long WsExNoActivate = 0x08000000L;
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwShowNoActivate = 4;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const int GwlStyle = -16;
    private const long FrameStyles = WsPopup | WsCaption | WsThickFrame | WsMinimizeBox |
                                      WsMaximizeBox | WsSysMenu | WsDlgFrame | WsBorder;
    private const long EmbeddedExStyles = WsExAppWindow | WsExWindowEdge | WsExDlgModalFrame |
                                          WsExClientEdge | WsExStaticEdge | WsExNoActivate;

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

    /// <summary>Returns the current native descendant that receives Roblox input.</summary>
    public nint? InputTargetFor(string accountId)
    {
        lock (_gate)
        {
            if (!_embedded.TryGetValue(accountId, out var embedded) || !IsCurrent(embedded, _hostWindow))
                return null;
            if (!IsValidInputTarget(embedded.Root, embedded.InputTarget, embedded.ProcessId))
                embedded.InputTarget = ResolveInputTarget(embedded.Root, embedded.ProcessId);
            return embedded.InputTarget;
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

    public bool TryEmbed(
        string accountId,
        nint rootWindow,
        int expectedProcessId,
        long expectedProcessStartTimeUtcTicks = 0,
        string? expectedProcessName = null,
        nint preferredInputWindow = default)
    {
        if (rootWindow == nint.Zero || expectedProcessId <= 0 || !IsWindow(rootWindow)) return false;
        GetWindowThreadProcessId(rootWindow, out var actualProcessId);
        if (actualProcessId != (uint)expectedProcessId) return false;
        if (!ValidateProcessIdentity(expectedProcessId, expectedProcessStartTimeUtcTicks, expectedProcessName)) return false;

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
        var originalExStyle = GetWindowLongPtr(rootWindow, GwlExStyle).ToInt64();
        var originalParent = GetParent(rootWindow);
        var originalOwner = GetWindow(rootWindow, GwOwner);
        if (!GetWindowRect(rootWindow, out var originalBounds)) return false;
        var originalPlacement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        var hasOriginalPlacement = GetWindowPlacement(rootWindow, ref originalPlacement);
        var originalVisible = IsWindowVisible(rootWindow);
        var childStyle = (originalStyle & ~FrameStyles) | WsChild | WsVisible | WsClipChildren | WsClipSiblings;
        var childExStyle = originalExStyle & ~EmbeddedExStyles;
        if (!TrySetStyle(rootWindow, childStyle)) return false;
        if (!TrySetExStyle(rootWindow, childExStyle))
        {
            TrySetStyle(rootWindow, originalStyle);
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(rootWindow, hostWindow);
        if (previousParent == nint.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            TrySetStyle(rootWindow, originalStyle);
            TrySetExStyle(rootWindow, originalExStyle);
            return false;
        }
        // Clear a stale owner left by a top-level Roblox frame. An owner can
        // keep the old popup in the activation/z-order chain after parenting.
        _ = SetWindowLongPtr(rootWindow, GwlpHwndParent, hostWindow);
        SetWindowPos(rootWindow, nint.Zero, 0, 0, 0, 0,
            SwpNoActivate | SwpNoZOrder | SwpNoMove | SwpNoSize | SwpFrameChanged);

        var embedded = new EmbeddedWindow(
            rootWindow,
            (uint)expectedProcessId,
            originalParent,
            originalOwner,
            originalStyle,
            originalExStyle,
            originalBounds,
            originalPlacement,
            hasOriginalPlacement,
            expectedProcessStartTimeUtcTicks,
            expectedProcessName,
            originalVisible);
        lock (_gate)
        {
            if (_hostWindow != hostWindow || !IsCurrent(embedded, hostWindow))
            {
                RestoreWindow(embedded);
                return false;
            }
            embedded.InputTarget = IsValidInputTarget(rootWindow, preferredInputWindow, (uint)expectedProcessId)
                ? preferredInputWindow
                : ResolveInputTarget(rootWindow, (uint)expectedProcessId);
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
            SetWindowPos(window.Root, nint.Zero, 0, 0, width, height,
                SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
        }
    }

    public void ShowOnly(string accountId)
    {
        // A selected client can be activated only when RAM already owns the
        // foreground. This lets a real click naturally activate the embedded
        // Roblox child while ensuring tab synchronization never steals focus
        // from an external game or application.
        var activateSelected = HostOwnsForeground();
        lock (_gate)
        {
            _visibleAccountId = _embedded.TryGetValue(accountId, out var selected) &&
                                IsCurrent(selected, _hostWindow)
                ? accountId
                : null;
            foreach (var (id, embedded) in _embedded)
            {
                if (!IsCurrent(embedded, _hostWindow))
                {
                    HideStaleWindow(embedded);
                    continue;
                }
                // Activate only on the already-foreground RAM path; the
                // external-foreground path remains strictly no-activate.
                ShowWindow(embedded.Root,
                    string.Equals(id, _visibleAccountId, StringComparison.Ordinal)
                        ? (activateSelected ? SwShow : SwShowNoActivate)
                        : SwHide);
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
        return processId == embedded.ProcessId && GetParent(embedded.Root) == hostWindow &&
               ValidateProcessIdentity((int)embedded.ProcessId, embedded.ProcessStartTimeUtcTicks, embedded.ExpectedProcessName);
    }

    private static bool ValidateProcessIdentity(int processId, long expectedStartTicks, string? expectedProcessName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            if (expectedStartTicks > 0 && process.StartTime.ToUniversalTime().Ticks != expectedStartTicks) return false;
            return string.IsNullOrWhiteSpace(expectedProcessName) ||
                   string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void HideStaleWindow(EmbeddedWindow embedded)
    {
        if (embedded.Root == nint.Zero || !IsWindow(embedded.Root)) return;
        GetWindowThreadProcessId(embedded.Root, out var processId);
        if (processId == embedded.ProcessId &&
            ValidateProcessIdentity((int)embedded.ProcessId, embedded.ProcessStartTimeUtcTicks, embedded.ExpectedProcessName))
            ShowWindow(embedded.Root, SwHide);
    }

    private static nint ResolveInputTarget(nint root, uint processId)
    {
        if (!IsWindow(root)) return nint.Zero;
        var best = root;
        var child = GetWindow(root, GwChild);
        while (child != nint.Zero)
        {
            GetWindowThreadProcessId(child, out var childProcessId);
            if (childProcessId == processId && IsWindowVisible(child) && IsWindowEnabled(child))
            {
                var nested = ResolveInputTarget(child, processId);
                if (nested != nint.Zero) best = nested;
            }
            child = GetWindow(child, GwNext);
        }
        return best;
    }

    private static bool IsValidInputTarget(nint root, nint target, uint processId)
    {
        if (target == nint.Zero || !IsWindow(target) || !IsWindowVisible(target) || !IsWindowEnabled(target) ||
            (target != root && !IsChild(root, target))) return false;
        GetWindowThreadProcessId(target, out var targetPid);
        return targetPid == processId;
    }

    private static bool TrySetStyle(nint window, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(window, GwlStyle, new nint(style));
        return previous != nint.Zero || Marshal.GetLastPInvokeError() == 0;
    }

    private static bool TrySetExStyle(nint window, long style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(window, GwlExStyle, new nint(style));
        return previous != nint.Zero || Marshal.GetLastPInvokeError() == 0;
    }

    private static void RestoreWindow(EmbeddedWindow embedded)
    {
        if (embedded.Root == nint.Zero || !IsWindow(embedded.Root)) return;
        GetWindowThreadProcessId(embedded.Root, out var processId);
        if (processId != embedded.ProcessId) return;

        RestorePointerState(embedded.Root);
        ShowWindow(embedded.Root, SwHide);
        SetParent(embedded.Root, embedded.OriginalParent);
        TrySetStyle(embedded.Root, embedded.OriginalStyle);
        TrySetExStyle(embedded.Root, embedded.OriginalExStyle);
        if (embedded.OriginalParent == nint.Zero)
            _ = SetWindowLongPtr(embedded.Root, GwlpHwndParent, embedded.OriginalOwner);
        if (embedded.HasOriginalPlacement)
            _ = SetWindowPlacement(embedded.Root, ref embedded.OriginalPlacement);
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

    private static void RestorePointerState(nint root)
    {
        var capture = GetCapture();
        if (capture != nint.Zero && (capture == root || IsChild(root, capture)))
            ReleaseCapture();

        if (!GetClipCursor(out var clip) || !GetWindowRect(root, out var bounds)) return;
        if (clip.Left == bounds.Left && clip.Top == bounds.Top &&
            clip.Right == bounds.Right && clip.Bottom == bounds.Bottom)
        {
            // Roblox may retain a client-area cursor clip while its window is
            // being detached. Clear only an exact clip owned by this client;
            // never disturb a user's unrelated foreground application's clip.
            ClipCursor(nint.Zero);
        }
    }

    private sealed class EmbeddedWindow
    {
        public EmbeddedWindow(
            nint root,
            uint processId,
            nint originalParent,
            nint originalOwner,
            long originalStyle,
            long originalExStyle,
            RECT originalBounds,
            WINDOWPLACEMENT originalPlacement,
            bool hasOriginalPlacement,
            long processStartTimeUtcTicks,
            string? expectedProcessName,
            bool originalVisible)
        {
            Root = root;
            ProcessId = processId;
            OriginalParent = originalParent;
            OriginalOwner = originalOwner;
            OriginalStyle = originalStyle;
            OriginalExStyle = originalExStyle;
            OriginalBounds = originalBounds;
            OriginalPlacement = originalPlacement;
            HasOriginalPlacement = hasOriginalPlacement;
            ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
            ExpectedProcessName = expectedProcessName;
            OriginalVisible = originalVisible;
            InputTarget = root;
        }

        public nint Root { get; }
        public uint ProcessId { get; }
        public nint OriginalParent { get; }
        public nint OriginalOwner { get; }
        public long OriginalStyle { get; }
        public long OriginalExStyle { get; }
        public RECT OriginalBounds { get; }
        public WINDOWPLACEMENT OriginalPlacement;
        public bool HasOriginalPlacement { get; }
        public long ProcessStartTimeUtcTicks { get; }
        public string? ExpectedProcessName { get; }
        public bool OriginalVisible { get; }
        public nint InputTarget { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint newParent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(nint rect);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    private const uint GwChild = 5;
    private const uint GwNext = 2;
}

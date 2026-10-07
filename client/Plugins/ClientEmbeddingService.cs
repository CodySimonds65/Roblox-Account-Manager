using System.Runtime.InteropServices;
using System.Diagnostics;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Docks managed Roblox top-level windows over the dedicated native Clients
/// host. Keeping the Roblox root top-level preserves Windows' normal
/// foreground, activation, capture, and raw-input behavior; macros continue
/// to use their separate safety brokers.
/// </summary>
public sealed class ClientEmbeddingService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EmbeddedWindow> _embedded = new(StringComparer.Ordinal);
    private string? _visibleAccountId;
    private readonly List<string> _tileOrder = [];
    private bool _tiled;
    private nint _hostWindow;
    private static readonly TimeSpan IdentityValidationCacheDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Decides whether an account is eligible for embedding/tab display; accounts returning false are unembedded.</summary>
    public Func<string, bool>? EmbedFilter { get; set; }

    /// <summary>Raised when the embedding eligibility of accounts may have changed.</summary>
    public event Action? FilterChanged;

    /// <summary>Optional read-only diagnostics for failed native docking invariants.</summary>
    public Action<string>? Diagnostics { get; set; }

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
    private const int SwShowNoActivate = 4;
    private const int SwpShowWindow = 0x0040;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpAsyncWindowPos = 0x4000;
    private const int SwpNoOwnerZOrder = 0x0200;
    private const uint GwHwndPrev = 3;
    private const long WsExTopmost = 0x00000008L;
    private static readonly nint HwndTop = nint.Zero;
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

    /// <summary>
    /// Grid mode: every docked client is shown at once, tiled inside the
    /// viewport in docking order. The selected client still receives
    /// automation, and only one client can own foreground at a time.
    /// Callers apply it through ShowOnly or Layout while the Clients view is
    /// visible, so setting it never shows a client over another view.
    /// </summary>
    public bool Tiled
    {
        get { lock (_gate) return _tiled; }
        set
        {
            lock (_gate)
            {
                if (_tiled == value) return;
                _tiled = value;
                foreach (var embedded in _embedded.Values) embedded.HasAppliedDockBounds = false;
            }
        }
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

    public nint? RootFor(string accountId)
    {
        lock (_gate)
        {
            return _embedded.TryGetValue(accountId, out var embedded) && IsCurrent(embedded, _hostWindow)
                ? embedded.Root
                : null;
        }
    }

    /// <summary>
    /// Returns the tracked dock root even when temporary docking state has
    /// drifted. Callers use this only to fail closed rather than fall back to
    /// a process root that could be visible outside the Clients viewport.
    /// </summary>
    public nint? TrackedRootFor(string accountId)
    {
        lock (_gate) return _embedded.TryGetValue(accountId, out var embedded) ? embedded.Root : null;
    }

    /// <summary>Returns true only when this selected Roblox top-level owns desktop foreground.</summary>
    public bool TargetOwnsForeground(string accountId)
    {
        lock (_gate)
        {
            return string.Equals(_visibleAccountId, accountId, StringComparison.Ordinal) &&
                   _embedded.TryGetValue(accountId, out var embedded) &&
                   IsCurrent(embedded, _hostWindow) && GetForegroundWindow() == embedded.Root;
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
        _ = preferredInputWindow; // Retained for call-site compatibility; the top-level root owns native input.
        if (rootWindow == nint.Zero || expectedProcessId <= 0 || !IsWindow(rootWindow)) return false;
        GetWindowThreadProcessId(rootWindow, out var actualProcessId);
        if (actualProcessId != (uint)expectedProcessId) return false;
        if (!ValidateProcessIdentity(expectedProcessId, expectedProcessStartTimeUtcTicks, expectedProcessName)) return false;

        EmbeddedWindow? existing;
        nint hostWindow;
        nint hostRoot;
        lock (_gate)
        {
            hostWindow = _hostWindow;
            hostRoot = hostWindow == nint.Zero ? nint.Zero : GetAncestor(hostWindow, GaRoot);
            _embedded.TryGetValue(accountId, out existing);
            if (existing is not null && existing.Root == rootWindow && IsCurrent(existing, hostWindow)) return true;
        }
        if (hostWindow == nint.Zero || !IsWindow(hostWindow) || hostRoot == nint.Zero || !IsWindow(hostRoot)) return false;
        if (existing is not null) TryUnembed(accountId);

        var originalStyle = GetWindowLongPtr(rootWindow, GwlStyle).ToInt64();
        var originalExStyle = GetWindowLongPtr(rootWindow, GwlExStyle).ToInt64();
        var originalParent = GetParent(rootWindow);
        var originalOwner = GetWindow(rootWindow, GwOwner);
        if (!GetWindowRect(rootWindow, out var originalBounds)) return false;
        var originalPlacement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        var hasOriginalPlacement = GetWindowPlacement(rootWindow, ref originalPlacement);
        var originalVisible = IsWindowVisible(rootWindow);

        // Only accept an already top-level Roblox root. Reparenting a child
        // changes GA_ROOT and can strand physical activation/raw input in RAM.
        // Reject before changing any style so the caller retains ownership of
        // a window we cannot safely restore.
        if ((originalStyle & WsChild) != 0 || GetAncestor(rootWindow, GaRoot) != rootWindow)
            return false;

        var dockedStyle = (originalStyle & ~(FrameStyles | WsChild)) | WsPopup | WsVisible;
        var childExStyle = originalExStyle & ~EmbeddedExStyles;
        if (!TrySetStyle(rootWindow, dockedStyle)) return false;
        if (!TrySetExStyle(rootWindow, childExStyle))
        {
            TrySetStyle(rootWindow, originalStyle);
            return false;
        }

        // Keep Roblox a true top-level window and leave its owner alone. Making
        // RAM's window its owner across processes implicitly attaches the two
        // GUI threads' input queues, so RAM's clicks and drags queued behind
        // Roblox's render loop. Z-order is kept by KeepAboveHost instead.
        SetWindowPos(rootWindow, nint.Zero, 0, 0, 0, 0,
            SwpNoActivate | SwpNoZOrder | SwpNoMove | SwpNoSize | SwpFrameChanged);

        var embedded = new EmbeddedWindow(
            accountId,
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
            hostRoot,
            originalVisible);
        if (GetWindow(rootWindow, GwOwner) != originalOwner ||
            GetAncestor(rootWindow, GaRoot) != rootWindow)
        {
            RestoreWindow(embedded);
            return false;
        }
        lock (_gate)
        {
            if (_hostWindow != hostWindow || !IsCurrent(embedded, hostWindow))
            {
                RestoreWindow(embedded);
                return false;
            }
            _embedded[accountId] = embedded;
            _tileOrder.Remove(accountId);
            _tileOrder.Add(accountId);
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
            _tileOrder.Remove(accountId);
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

    // deferResize keeps the visible client at its current size while the user
    // is dragging a window edge or splitter: it only follows the viewport's
    // origin and is clipped to it. Roblox rebuilds its swap chain on every
    // resize, so resizing it per drag step is what made the drag feel slow.
    // The final size is applied once the drag ends.
    public void Layout(bool deferResize = false)
    {
        nint hostWindow;
        EmbeddedWindow[] embedded;
        string? visibleAccountId;
        bool tiled;
        string[] tileOrder;
        lock (_gate)
        {
            hostWindow = _hostWindow;
            embedded = _embedded.Values.ToArray();
            visibleAccountId = _visibleAccountId;
            tiled = _tiled;
            tileOrder = _tileOrder.Where(_embedded.ContainsKey).ToArray();
        }
        if (hostWindow == nint.Zero || !IsWindow(hostWindow) || !GetClientRect(hostWindow, out var rect)) return;
        var hostOrigin = new POINT();
        if (!ClientToScreen(hostWindow, ref hostOrigin)) return;
        var hostWidth = rect.Right - rect.Left;
        var hostHeight = rect.Bottom - rect.Top;
        if (hostWidth < 64 || hostHeight < 64) return;

        // HideAll clears the selection, so a tiled layout shows nothing until
        // the Clients view selects a client again.
        var tiles = new Dictionary<string, LayoutRect>(StringComparer.Ordinal);
        if (tiled && visibleAccountId is not null)
        {
            var tiledIds = tileOrder.Where(id => embedded.Any(window => window.AccountId == id && IsCurrent(window, hostWindow))).ToArray();
            var cells = GridLayout.Tile(new LayoutRect(hostOrigin.X, hostOrigin.Y, hostWidth, hostHeight), tiledIds.Length);
            for (var index = 0; index < tiledIds.Length; index++) tiles[tiledIds[index]] = cells[index];
        }

        foreach (var window in embedded)
        {
            if (!IsCurrent(window, hostWindow))
            {
                HideManagedWindowIfIdentityValid(window);
                continue;
            }
            var selected = tiled
                ? tiles.ContainsKey(window.AccountId)
                : string.Equals(window.AccountId, visibleAccountId, StringComparison.Ordinal);
            if (!selected)
            {
                if (IsWindowVisible(window.Root)) HideWindow(window.Root);
                ClearClip(window);
                window.HasAppliedDockBounds = false;
                continue;
            }
            if (IsWindowVisible(window.Root)) KeepAboveHost(window);
            var origin = hostOrigin;
            var width = hostWidth;
            var height = hostHeight;
            if (tiled)
            {
                var cell = tiles[window.AccountId];
                origin = new POINT { X = cell.Left, Y = cell.Top };
                width = cell.Width;
                height = cell.Height;
            }
            var target = new RECT(origin.X, origin.Y, origin.X + width, origin.Y + height);
            if (window.HasAppliedDockBounds && window.LastAppliedDockBounds.Equals(target) && !window.HasClipRegion &&
                IsWindowVisible(window.Root))
                continue;
            if (deferResize && window.HasAppliedDockBounds && IsWindowVisible(window.Root) &&
                TryFollowWithoutResize(window, origin, width, height))
                continue;
            ClearClip(window);
            // A cross-process SetWindowPos waits for Roblox to handle the move,
            // including its swap-chain resize, so every step of a live resize
            // stalled RAM's UI thread. Once the client is visible, post the
            // geometry change instead. The async request omits SWP_SHOWWINDOW
            // so a late delivery can never re-show a client hidden since.
            if (IsWindowVisible(window.Root))
            {
                if (!SetWindowPos(window.Root, nint.Zero, target.Left, target.Top, width, height,
                        SwpNoActivate | SwpNoZOrder | SwpAsyncWindowPos))
                {
                    Diagnostics?.Invoke($"Dock layout failed for {window.AccountId} (Win32 {Marshal.GetLastWin32Error()}).");
                    continue;
                }
                window.LastAppliedDockBounds = target;
                window.HasAppliedDockBounds = true;
                continue;
            }
            if (!SetWindowPos(window.Root, nint.Zero, target.Left, target.Top, width, height,
                    SwpNoActivate | SwpNoZOrder | SwpShowWindow))
            {
                Diagnostics?.Invoke($"Dock layout failed for {window.AccountId} (Win32 {Marshal.GetLastWin32Error()}).");
                continue;
            }
            window.LastAppliedDockBounds = target;
            window.HasAppliedDockBounds = true;
            KeepAboveHost(window);
            if (!GetWindowRect(window.Root, out var actual) ||
                actual.Left != origin.X || actual.Top != origin.Y ||
                actual.Right != origin.X + width || actual.Bottom != origin.Y + height)
            {
                Diagnostics?.Invoke($"Dock layout mismatch for {window.AccountId}: expected {origin.X},{origin.Y},{width},{height}; " +
                                    $"actual {actual.Left},{actual.Top},{actual.Right - actual.Left},{actual.Bottom - actual.Top}.");
            }
        }
    }

    // Re-stacks the visible client directly above RAM after RAM's own z-order
    // changes (for example when a click activates RAM). Without an owner,
    // Windows no longer does this for us.
    public void KeepSelectedAboveHost()
    {
        EmbeddedWindow[] candidates;
        nint hostWindow;
        lock (_gate)
        {
            hostWindow = _hostWindow;
            if (_visibleAccountId is null) candidates = [];
            else if (_tiled) candidates = _embedded.Values.ToArray();
            else candidates = _embedded.TryGetValue(_visibleAccountId, out var window) ? [window] : [];
        }
        foreach (var candidate in candidates)
        {
            if (IsCurrent(candidate, hostWindow) && IsWindowVisible(candidate.Root)) KeepAboveHost(candidate);
        }
    }

    private static void KeepAboveHost(EmbeddedWindow window)
    {
        var above = GetWindow(window.HostRoot, GwHwndPrev);
        if (above == window.Root) return;
        // Inserting below a topmost window would make the client topmost too.
        var insertAfter = above == nint.Zero ||
                          ((GetWindowLongPtr(above, GwlExStyle).ToInt64() & WsExTopmost) != 0 &&
                           (GetWindowLongPtr(window.HostRoot, GwlExStyle).ToInt64() & WsExTopmost) == 0)
            ? HwndTop
            : above;
        // Async so a busy Roblox thread never stalls RAM; no SWP_SHOWWINDOW,
        // so it cannot re-show a client hidden in the meantime.
        _ = SetWindowPos(window.Root, insertAfter, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpAsyncWindowPos);
    }

    private bool TryFollowWithoutResize(EmbeddedWindow window, POINT origin, int width, int height)
    {
        var applied = window.LastAppliedDockBounds;
        var keptWidth = applied.Right - applied.Left;
        var keptHeight = applied.Bottom - applied.Top;
        var clipWidth = Math.Min(width, keptWidth);
        var clipHeight = Math.Min(height, keptHeight);
        if (clipWidth == keptWidth && clipHeight == keptHeight)
        {
            ClearClip(window);
        }
        else if (!window.HasClipRegion || window.ClipWidth != clipWidth || window.ClipHeight != clipHeight)
        {
            // Without the clip a shrinking viewport would leave the owned
            // client drawn over RAM's sidebar and activity panel mid-drag.
            var region = CreateRectRgn(0, 0, clipWidth, clipHeight);
            if (region == nint.Zero) return false;
            // redraw: false keeps SetWindowRgn from sending Roblox synchronous
            // WM_WINDOWPOSCHANGING/CHANGED messages on every drag step.
            if (SetWindowRgn(window.Root, region, false) == 0)
            {
                DeleteObject(region);
                return false;
            }
            window.HasClipRegion = true;
            window.ClipWidth = clipWidth;
            window.ClipHeight = clipHeight;
        }

        if (applied.Left != origin.X || applied.Top != origin.Y)
        {
            if (!SetWindowPos(window.Root, nint.Zero, origin.X, origin.Y, 0, 0,
                    SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpAsyncWindowPos))
                return false;
            window.LastAppliedDockBounds = new RECT(origin.X, origin.Y, origin.X + keptWidth, origin.Y + keptHeight);
        }
        return true;
    }

    private static void ClearClip(EmbeddedWindow window)
    {
        if (!window.HasClipRegion) return;
        _ = SetWindowRgn(window.Root, nint.Zero, false);
        window.HasClipRegion = false;
    }

    public void ShowOnly(string accountId)
    {
        // Selection changes visibility and geometry only. A physical click on
        // the docked top-level Roblox window owns the activation path.
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
                if (_tiled || string.Equals(id, _visibleAccountId, StringComparison.Ordinal))
                {
                    embedded.HasAppliedDockBounds = false;
                }
                else
                {
                    if (IsWindowVisible(embedded.Root)) HideWindow(embedded.Root);
                    embedded.HasAppliedDockBounds = false;
                }
            }
        }
        Layout();
    }

    public void HideAll()
    {
        lock (_gate)
        {
            _visibleAccountId = null;
            foreach (var embedded in _embedded.Values)
            {
                if (IsCurrent(embedded, _hostWindow)) HideWindow(embedded.Root);
            }
        }
    }

    public void HideRoot(string accountId)
    {
        lock (_gate)
        {
            if (_embedded.TryGetValue(accountId, out var embedded) && IsCurrent(embedded, _hostWindow))
                HideWindow(embedded.Root);
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
        return processId == embedded.ProcessId &&
               GetAncestor(hostWindow, GaRoot) == embedded.HostRoot &&
               GetWindow(embedded.Root, GwOwner) == embedded.OriginalOwner &&
               GetAncestor(embedded.Root, GaRoot) == embedded.Root &&
               (GetWindowLongPtr(embedded.Root, GwlStyle).ToInt64() & WsChild) == 0 &&
               (GetWindowLongPtr(embedded.Root, GwlStyle).ToInt64() & WsPopup) != 0 &&
               ValidateProcessIdentityCached(embedded);
    }

    private static bool ValidateProcessIdentityCached(EmbeddedWindow embedded)
    {
        var now = DateTime.UtcNow;
        if (embedded.IdentityValidatedUntilUtc > now) return embedded.IdentityValid;
        var valid = ValidateProcessIdentity((int)embedded.ProcessId, embedded.ProcessStartTimeUtcTicks, embedded.ExpectedProcessName);
        embedded.IdentityValid = valid;
        embedded.IdentityValidatedUntilUtc = now + IdentityValidationCacheDuration;
        return valid;
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
        HideManagedWindowIfIdentityValid(embedded);
    }

    private static void HideManagedWindowIfIdentityValid(EmbeddedWindow embedded)
    {
        if (HasImmutableIdentity(embedded)) HideWindow(embedded.Root);
    }

    private static bool HasImmutableIdentity(EmbeddedWindow embedded)
    {
        if (embedded.Root == nint.Zero || !IsWindow(embedded.Root)) return false;
        GetWindowThreadProcessId(embedded.Root, out var processId);
        return processId == embedded.ProcessId &&
               ValidateProcessIdentity((int)embedded.ProcessId, embedded.ProcessStartTimeUtcTicks, embedded.ExpectedProcessName) &&
               GetAncestor(embedded.Root, GaRoot) == embedded.Root &&
               (GetWindowLongPtr(embedded.Root, GwlStyle).ToInt64() & WsChild) == 0;
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
        if (!HasImmutableIdentity(embedded)) return;

        HideWindow(embedded.Root);
        ClearClip(embedded);
        TrySetStyle(embedded.Root, embedded.OriginalStyle);
        TrySetExStyle(embedded.Root, embedded.OriginalExStyle);
        // TryEmbed accepts only top-level roots and never changes the owner,
        // so restoring both is a no-op unless something else drifted them.
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
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foreground != root && foregroundProcessId != (uint)Environment.ProcessId) return;
        if (!GetClipCursor(out var clip) || !GetWindowRect(root, out var bounds)) return;
        if (clip.Left >= bounds.Left && clip.Top >= bounds.Top &&
            clip.Right <= bounds.Right && clip.Bottom <= bounds.Bottom)
        {
            // A foreground Roblox client may use either its full client area
            // or a small center rectangle for mouse lock. Clear only a clip
            // fully contained by that validated foreground root.
            ClipCursor(nint.Zero);
        }
    }

    private static void HideWindow(nint root)
    {
        RestorePointerState(root);
        ShowWindow(root, SwHide);
    }

    private sealed class EmbeddedWindow
    {
        public EmbeddedWindow(
            string accountId,
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
            nint hostRoot,
            bool originalVisible)
        {
            AccountId = accountId;
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
            HostRoot = hostRoot;
            OriginalVisible = originalVisible;
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
        public nint HostRoot { get; }
        public string AccountId { get; }
        public bool OriginalVisible { get; }
        public DateTime IdentityValidatedUntilUtc { get; set; }
        public bool IdentityValid { get; set; }
        public bool HasAppliedDockBounds { get; set; }
        public RECT LastAppliedDockBounds { get; set; }
        public bool HasClipRegion { get; set; }
        public int ClipWidth { get; set; }
        public int ClipHeight { get; set; }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(nint rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref POINT point);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint window, ref WINDOWPLACEMENT placement);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT
    {
        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

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

}

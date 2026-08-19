using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Mirrors only activation state and keyboard focus across the WPF/Win32
/// boundary. Human mouse and keyboard events travel directly to the embedded
/// Roblox HWND through the native host and are never synthesized here.
/// </summary>
public static class EmbeddedInputBridge
{
    private const int WmActivate = 0x0006;
    private const int WmActivateApp = 0x001C;
    private const int WaInactive = 0;
    private const uint GaRoot = 2;
    private const uint SmtoAbortIfHung = 0x0002;

    private static HwndSource? _source;
    private static Func<string?>? _visibleAccount;
    private static Func<string, nint?>? _rootResolver;
    private static Func<string, nint?>? _inputTargetResolver;

    public static Action<string>? Diagnostics { get; set; }

    public static void Attach(
        nint foregroundWindow,
        Func<string?> visibleAccount,
        Func<string, nint?> rootResolver,
        Func<string, nint?>? inputTargetResolver = null)
    {
        Detach();
        _visibleAccount = visibleAccount;
        _rootResolver = rootResolver;
        _inputTargetResolver = inputTargetResolver;
        _source = HwndSource.FromHwnd(foregroundWindow);
        _source?.AddHook(WndProc);
    }

    public static void Detach()
    {
        if (_source is not null) _source.RemoveHook(WndProc);
        _source = null;
        _visibleAccount = null;
        _rootResolver = null;
        _inputTargetResolver = null;
    }

    public static bool FocusEmbedded(nint root)
    {
        if (root == nint.Zero || !IsWindow(root) || !IsWindowVisible(root))
        {
            Diagnostics?.Invoke("Embedded focus: the visible client window is unavailable.");
            return false;
        }

        // Focus is allowed only for a client already hosted by the current
        // foreground top-level window. SetFocus itself does not change the
        // desktop foreground window, and this check prevents it from being
        // used to pull RAM over another game or application.
        var foregroundRoot = GetAncestor(root, GaRoot);
        if (foregroundRoot == nint.Zero || GetForegroundWindow() != foregroundRoot)
        {
            Diagnostics?.Invoke("Embedded focus: RAM does not own the foreground window.");
            return false;
        }

        var target = ResolveInputTarget(root);
        if (target == nint.Zero || !IsWindow(target) || !IsWindowVisible(target) || !IsWindowEnabled(target) ||
            !IsFocusWithin(root, target))
        {
            Diagnostics?.Invoke("Embedded focus: the native Roblox input target is unavailable.");
            return false;
        }

        var gameThread = GetWindowThreadProcessId(target, out var targetProcessId);
        GetWindowThreadProcessId(root, out var rootProcessId);
        if (gameThread == 0 || targetProcessId == 0 || rootProcessId != targetProcessId)
        {
            Diagnostics?.Invoke("Embedded focus: the input target process identity changed.");
            return false;
        }
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (gameThread != 0 && GetGUIThreadInfo(gameThread, ref info) &&
            IsFocusWithin(root, info.hwndFocus) && IsFocusWithin(root, info.hwndActive)) return true;

        var ourThread = GetCurrentThreadId();
        var attached = gameThread != 0 && gameThread != ourThread &&
                       AttachThreadInput(ourThread, gameThread, true);
        try
        {
            // SetFocus is only valid across threads while their input queues
            // are attached. The attachment is deliberately scoped to this
            // operation and is always undone below.
            if (GetForegroundWindow() != foregroundRoot ||
                GetWindowThreadProcessId(root, out var currentRootProcessId) == 0 ||
                currentRootProcessId != rootProcessId || GetParent(root) == nint.Zero)
            {
                Diagnostics?.Invoke("Embedded focus: the client changed while focus was being prepared.");
                return false;
            }

            _ = SetFocus(target);
            info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            var actual = gameThread != 0 && GetGUIThreadInfo(gameThread, ref info) ? info.hwndFocus : nint.Zero;
            Diagnostics?.Invoke($"Embedded focus 0x{root.ToInt64():X}/target 0x{target.ToInt64():X}: attached={(attached ? "yes" : "no")}, actual focus 0x{actual.ToInt64():X}.");
            return IsFocusWithin(root, actual);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, gameThread, false);
        }
    }

    /// <summary>Returns whether the supplied embedded root currently owns keyboard focus.</summary>
    public static bool HasFocusWithin(nint root)
    {
        if (root == nint.Zero || !IsWindow(root)) return false;
        var thread = GetWindowThreadProcessId(root, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && IsFocusWithin(root, info.hwndFocus);
    }

    public static void TransferFocus(nint? previousRoot, nint currentRoot, bool hostForeground)
    {
        if (hostForeground) FocusEmbedded(currentRoot);
    }

    private static nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmActivateApp)
        {
            QueueActivation(wParam != nint.Zero);
        }
        else if (message == WmActivate)
        {
            var active = unchecked((ushort)(long)wParam) != WaInactive;
            QueueActivation(active);
        }
        return nint.Zero;
    }

    private static void QueueActivation(bool active)
    {
        // WM_ACTIVATE is delivered before the foreground transition is fully
        // observable from another thread. Deferring one dispatcher turn avoids
        // incorrectly treating the selected client as inactive and skipping
        // the focus handoff.
        var dispatcher = _source?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ForwardActivation(active)));
    }

    private static void ForwardActivation(bool active)
    {
        var root = ResolveVisibleRoot();
        if (root == nint.Zero) return;
        var foregroundRoot = _source is null ? nint.Zero : GetAncestor(_source.Handle, GaRoot);
        var foreground = active && foregroundRoot != nint.Zero && GetForegroundWindow() == foregroundRoot;
        if (foreground) FocusEmbedded(root);
    }

    private static nint ResolveVisibleRoot()
    {
        var accountId = _visibleAccount?.Invoke();
        if (accountId is null) return nint.Zero;
        var root = _rootResolver?.Invoke(accountId);
        return root is not null && root != nint.Zero && IsWindow(root.Value) && IsWindowVisible(root.Value)
            ? root.Value
            : nint.Zero;
    }

    private static nint ResolveInputTarget(nint root)
    {
        var accountId = _visibleAccount?.Invoke();
        var resolved = accountId is null ? null : _inputTargetResolver?.Invoke(accountId);
        if (resolved is not null && resolved.Value != nint.Zero && IsFocusWithin(root, resolved.Value))
            return resolved.Value;
        return FindDeepestInputDescendant(root);
    }

    private static nint FindDeepestInputDescendant(nint root)
    {
        var best = root;
        var child = GetWindow(root, GwChild);
        while (child != nint.Zero)
        {
            if (IsWindow(child) && IsWindowVisible(child) && IsWindowEnabled(child) && IsFocusWithin(root, child))
            {
                best = FindDeepestInputDescendant(child);
                break;
            }
            child = GetWindow(child, GwNext);
        }
        return best;
    }

    private static bool IsFocusWithin(nint root, nint focusedWindow) =>
        focusedWindow != nint.Zero && (focusedWindow == root || IsChild(root, focusedWindow));

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    private const uint GwChild = 5;
    private const uint GwNext = 2;

    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }
}

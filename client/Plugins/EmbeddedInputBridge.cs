using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Bridges focus, activation, and pointer input from the launcher window to the
/// embedded game windows. WPF's input pipeline consumes pointer messages that
/// land on its own visuals (the host area has a WPF background), so a native
/// WS_CHILD game window never sees them: every client-area mouse message whose
/// screen point falls inside the visible embedded root is re-posted to that
/// window with client coordinates, and the message is marked handled so WPF
/// does not also process it.
/// Note: posted messages carry a synthesized GetMessagePos/GetMessageTime, so
/// engine paths that read lParam (Roblox/SDL/Chromium) work, while paths that
/// query the cursor queue position do not.
/// </summary>
public static class EmbeddedInputBridge
{
    private const int WmMouseFirst = 0x0200;
    private const int WmMouseLast = 0x020E;
    private const int WmMouseMove = 0x0200;
    private const int WmActivate = 0x0006;
    private const int WmActivateApp = 0x001C;
    private const int WmMouseActivate = 0x0021;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHwheel = 0x020E;
    private const int WmMouseLeave = 0x02A3;
    private const nint WaActive = 1;
    private const nint WaInactive = 0;

    private static HwndSource? _source;
    private static Func<string?>? _visibleAccount;
    private static Func<string, nint?>? _rootResolver;
    private static int _pendingButtonDown;
    private static bool _cursorInsideLastMove;

    public static Action<string>? Diagnostics { get; set; }

    public static void Attach(nint hostHwnd, Func<string?> visibleAccount, Func<string, nint?> rootResolver)
    {
        Detach();
        _visibleAccount = visibleAccount;
        _rootResolver = rootResolver;
        _source = HwndSource.FromHwnd(hostHwnd);
        _source?.AddHook(WndProc);
    }

    public static void Detach()
    {
        if (_source is not null) _source.RemoveHook(WndProc);
        _source = null;
        _visibleAccount = null;
        _rootResolver = null;
        _pendingButtonDown = 0;
        _cursorInsideLastMove = false;
    }

    public static bool FocusEmbedded(nint root)
    {
        if (root == nint.Zero || !IsWindow(root))
        {
            Diagnostics?.Invoke("Embedded focus: the client window no longer exists.");
            return false;
        }

        // If the game thread already owns focus on the embedded root, there is
        // nothing to do; attaching input threads redistributes key state and is
        // only needed when focus actually has to move.
        var gameThread = GetWindowThreadProcessId(root, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (gameThread != 0 && GetGUIThreadInfo(gameThread, ref info) && info.hwndFocus == root)
        {
            return true;
        }

        var ourThread = GetCurrentThreadId();
        var attached = gameThread != 0 && gameThread != ourThread &&
                       AttachThreadInput(ourThread, gameThread, true);
        try
        {
            SetFocus(root);
            var actual = GetFocus();
            Diagnostics?.Invoke($"Embedded focus 0x{root.ToInt64():X}: attached={(attached ? "yes" : "no")}, actual focus 0x{actual.ToInt64():X}.");
            return actual == root;
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, gameThread, false);
        }
    }

    private static nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is WmActivateApp or WmActivate or WmMouseActivate)
        {
            ForwardActivation(wParam);
            return nint.Zero;
        }

        if (message >= WmMouseFirst && message <= WmMouseLast)
        {
            if (ForwardMouseMessage(hwnd, message, wParam, lParam))
            {
                handled = true;
            }
        }
        return nint.Zero;
    }

    private static void ForwardActivation(nint wParam)
    {
        var accountId = _visibleAccount?.Invoke();
        if (accountId is null) return;
        var root = _rootResolver?.Invoke(accountId);
        if (root is null || root == nint.Zero || !IsWindow(root.Value)) return;
        var activated = wParam == WaActive || GetForegroundWindow() == _source?.Handle;
        try
        {
            SendMessage(root.Value, WmActivateApp, activated ? WaActive : WaInactive, nint.Zero);
            SendMessage(root.Value, WmActivate, activated ? WaActive : WaInactive, _source?.Handle ?? nint.Zero);
        }
        catch
        {
            // The game may have closed between the check and the send.
        }
        if (activated) FocusEmbedded(root.Value);
    }

    private static bool ForwardMouseMessage(nint hostHwnd, int message, nint wParam, nint lParam)
    {
        var accountId = _visibleAccount?.Invoke();
        if (accountId is null) return false;
        var root = _rootResolver?.Invoke(accountId);
        if (root is null || root == nint.Zero || !IsWindow(root.Value) || !IsWindowVisible(root.Value)) return false;

        var isWheel = message is WmMouseWheel or WmMouseHwheel;
        // Pointer messages carry host-client coordinates; wheel messages carry
        // screen coordinates already.
        var hostX = unchecked((short)(long)lParam);
        var hostY = unchecked((short)((long)lParam >> 16));
        var point = new POINT { X = hostX, Y = hostY };
        if (!isWheel)
        {
            ClientToScreen(hostHwnd, ref point);
        }

        ScreenToClient(root.Value, ref point);
        var inside = GetClientRect(root.Value, out var rect) &&
                     point.X >= 0 && point.Y >= 0 && point.X < rect.Right && point.Y < rect.Bottom;

        // A button pressed inside the embed must always receive its release,
        // even if the cursor leaves the game area before letting go; otherwise
        // the game keeps a stuck button and WPF keeps a stuck capture.
        var pendingDown = _pendingButtonDown;
        var isUpMessage = message is WmLButtonUp or WmRButtonUp or WmMButtonUp or WmXButtonUp;
        if (!inside && !isWheel)
        {
            var completesPendingDown = (message == WmLButtonUp && pendingDown == WmLButtonDown) ||
                                       (message == WmRButtonUp && pendingDown == WmRButtonDown) ||
                                       (message == WmMButtonUp && pendingDown == WmMButtonDown) ||
                                       (message == WmXButtonUp && pendingDown == WmXButtonDown);
            if (!completesPendingDown)
            {
                if (_cursorInsideLastMove)
                {
                    _cursorInsideLastMove = false;
                    PostMessage(root.Value, WmMouseLeave, nint.Zero, nint.Zero);
                }
                return false;
            }
        }

        if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown)
        {
            _pendingButtonDown = message;
        }
        else if (isUpMessage)
        {
            _pendingButtonDown = 0;
        }
        if (message == WmMouseMove)
        {
            _cursorInsideLastMove = inside;
        }

        // Swallowing a press while WPF holds capture strands the capture and
        // the element's visual state; drop it before the game takes over.
        if ((message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown or WmLButtonUp or WmRButtonUp or WmMButtonUp or WmXButtonUp) &&
            Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        var gameParam = isWheel ? lParam : new nint((point.Y << 16) | (point.X & 0xFFFF));
        if (!PostMessage(root.Value, message, wParam, gameParam))
        {
            Diagnostics?.Invoke($"Embedded input: posting message 0x{message:X} to 0x{root.Value.ToInt64():X} failed.");
            return false;
        }

        if ((message is WmLButtonDown or WmRButtonDown or WmMButtonDown))
        {
            FocusEmbedded(root.Value);
        }
        return true;
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref POINT point);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    private struct POINT { public int X; public int Y; }

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

using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Bridges focus and activation state from the launcher window to the embedded
/// game windows so their engines see themselves as active and focused. Games
/// check activation before accepting input; embedded WS_CHILD windows never
/// receive WM_ACTIVATEAPP/WM_ACTIVATE on their own, so the launcher mirrors
/// those messages and re-focuses the visible child.
/// </summary>
public static class EmbeddedInputBridge
{
    private const int WmActivate = 0x0006;
    private const int WmActivateApp = 0x001C;
    private const int WmMouseActivate = 0x0021;
    private const nint WaActive = 1;
    private const nint WaInactive = 0;

    private static HwndSource? _source;
    private static Func<string?>? _visibleAccount;
    private static Func<string, nint?>? _rootResolver;
    private static nint _hostWindow;

    public static Action<string>? Diagnostics { get; set; }

    public static void Attach(nint hostHwnd, Func<string?> visibleAccount, Func<string, nint?> rootResolver)
    {
        Detach();
        _hostWindow = hostHwnd;
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
        _hostWindow = nint.Zero;
    }

    public static bool FocusEmbedded(nint root)
    {
        if (root == nint.Zero || !IsWindow(root))
        {
            Diagnostics?.Invoke("Embedded focus: the client window no longer exists.");
            return false;
        }
        var ourThread = GetCurrentThreadId();
        GetWindowThreadProcessId(root, out var gameThread);
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
        }
        return nint.Zero;
    }

    private static void ForwardActivation(nint wParam)
    {
        var accountId = _visibleAccount?.Invoke();
        if (accountId is null) return;
        var root = _rootResolver?.Invoke(accountId);
        if (root is null || root == nint.Zero || !IsWindow(root.Value)) return;
        var activated = wParam == WaActive || GetForegroundWindow() == _hostWindow;
        try
        {
            SendMessage(root.Value, WmActivateApp, activated ? WaActive : WaInactive, nint.Zero);
            SendMessage(root.Value, WmActivate, activated ? WaActive : WaInactive, _hostWindow);
        }
        catch
        {
            // The game may have closed between the check and the send.
        }
        if (activated) FocusEmbedded(root.Value);
    }

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);
}

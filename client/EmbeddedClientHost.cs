using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RobloxAltClient;

/// <summary>
/// Reserves a real Win32 child-window region inside WPF. Embedded Roblox HWNDs
/// are parented beneath this window so Windows, rather than WPF message
/// forwarding, delivers normal mouse and keyboard input to the game.
/// </summary>
public sealed class EmbeddedClientHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WmSize = 0x0005;
    private const int WmSetFocus = 0x0007;
    private const int WmMouseActivate = 0x0021;

    public nint NativeHandle { get; private set; }

    public event Action<nint>? HandleCreated;
    public event Action<nint>? HandleDestroying;
    public event Action? NativeSizeChanged;

    /// <summary>Focuses the currently visible Roblox child without activating
    /// RAM. The callback must return false when RAM does not own foreground.</summary>
    public Func<bool>? FocusVisibleClient { get; set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (handle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The native embedded-client host could not be created.");

        NativeHandle = handle;
        HandleCreated?.Invoke(handle);
        return new HandleRef(this, handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        var handle = hwnd.Handle;
        if (handle != nint.Zero) HandleDestroying?.Invoke(handle);
        NativeHandle = nint.Zero;
        if (handle != nint.Zero && IsWindow(handle)) DestroyWindow(handle);
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmSize)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => NativeSizeChanged?.Invoke()));
        }
        else if (message is WmMouseActivate or WmSetFocus)
        {
            // WM_MOUSEACTIVATE arrives before the top-level activation completes.
            // Defer focus so the guard can verify RAM is actually foreground.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusVisibleClient?.Invoke()));
        }

        return nint.Zero;
    }

    protected override bool TabIntoCore(TraversalRequest request) => FocusVisibleClient?.Invoke() == true;

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}

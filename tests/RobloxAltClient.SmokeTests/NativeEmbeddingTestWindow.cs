using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class NativeEmbeddingTestWindow : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsChild = 0x40000000;
    private const int WsClipChildren = 0x02000000;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int GwOwner = 4;
    private const int GaRoot = 2;

    private NativeEmbeddingTestWindow(nint handle)
    {
        Handle = handle;
    }

    public nint Handle { get; private set; }
    public long Style => GetWindowLongPtr(Handle, GwlStyle).ToInt64();
    public long ExStyle => GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
    public nint Parent => GetParent(Handle);
    public nint Owner => GetWindow(Handle, GwOwner);
    public nint Root => GetAncestor(Handle, GaRoot);
    public bool Visible => IsWindowVisible(Handle);
    public WindowBounds Bounds
    {
        get
        {
            if (!GetWindowRect(Handle, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
    }

    public bool HasChildStyle => (Style & WsChild) != 0;
    public bool HasPopupStyle => (Style & WsPopup) != 0;

    public void Show() => ShowWindow(Handle, 5);
    public void SetOwner(nint owner) => SetWindowLongPtr(Handle, -8, owner);

    public static NativeEmbeddingTestWindow CreateHost() => Create(WsPopup | WsVisible | WsClipChildren, -32000, -32000, 640, 480);

    public static NativeEmbeddingTestWindow CreateOwner() => Create(WsPopup | WsVisible, -32100, -32100, 320, 240);

    public static NativeEmbeddingTestWindow CreateRoot(int x, int y, int width, int height, nint owner = default) =>
        Create(WsPopup | WsVisible, x, y, width, height, owner);

    private static NativeEmbeddingTestWindow Create(int style, int x, int y, int width, int height, nint owner = default)
    {
        var handle = CreateWindowEx(
            WsExNoActivate,
            "static",
            string.Empty,
            style,
            x,
            y,
            width,
            height,
            owner,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (handle == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new NativeEmbeddingTestWindow(handle);
    }

    public void Dispose()
    {
        var handle = Handle;
        Handle = nint.Zero;
        if (handle != nint.Zero && IsWindow(handle)) DestroyWindow(handle);
    }

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
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom);

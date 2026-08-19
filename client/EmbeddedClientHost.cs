using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RobloxAltClient;

/// <summary>
/// Owns the native viewport boundary used by the Clients view. Roblox remains
/// a validated top-level owned window docked over this viewport, so Windows
/// delivers physical input to Roblox without WPF forwarding messages.
/// </summary>
public sealed class EmbeddedClientHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsTabStop = 0x00010000;
    private const int CsOwnDc = 0x0020;
    private const int WmNcCreate = 0x0081;
    private const int WmNcDestroy = 0x0082;
    private const int WmSize = 0x0005;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int GwlpUserData = -21;

    private static readonly HostWindowProc NativeWindowProc = StaticWindowProc;
    private static readonly nint ModuleHandle = GetModuleHandle(null);
    private static readonly string NativeClassName = $"RobloxAltClient.EmbeddedHost.{Environment.ProcessId}";
    private static int _classRegistered;

    private GCHandle _selfHandle;
    public nint NativeHandle { get; private set; }

    public event Action<nint>? HandleCreated;
    public event Action<nint>? HandleDestroying;
    public event Action? NativeSizeChanged;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureNativeClass();
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var handle = CreateWindowEx(
            0,
            NativeClassName,
            string.Empty,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings | WsTabStop,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            nint.Zero,
            ModuleHandle,
            GCHandle.ToIntPtr(_selfHandle));

        if (handle == nint.Zero)
        {
            _selfHandle.Free();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The native embedded-client host could not be created.");
        }

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
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        HandleNativeMessage(message, wParam, lParam, out handled);

    private nint HandleNativeMessage(int message, nint wParam, nint lParam, out bool handled)
    {
        handled = false;
        switch (message)
        {
            case WmNcHitTest:
                handled = true;
                return new nint(HtClient);

            case WmSize:
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => NativeSizeChanged?.Invoke()));
                break;
        }

        return nint.Zero;
    }

    private static nint StaticWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        EmbeddedClientHost? host = null;
        if (message == WmNcCreate)
        {
            var create = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
            if (create.lpCreateParams != nint.Zero)
            {
                var handle = GCHandle.FromIntPtr(create.lpCreateParams);
                host = handle.Target as EmbeddedClientHost;
                SetWindowLongPtr(hwnd, GwlpUserData, create.lpCreateParams);
            }
        }
        else
        {
            var pointer = GetWindowLongPtr(hwnd, GwlpUserData);
            if (pointer != nint.Zero)
            {
                try { host = GCHandle.FromIntPtr(pointer).Target as EmbeddedClientHost; }
                catch (InvalidOperationException) { host = null; }
            }
        }

        if (host is not null)
        {
            var result = host.HandleNativeMessage(unchecked((int)message), wParam, lParam, out var handled);
            if (message == WmNcDestroy) SetWindowLongPtr(hwnd, GwlpUserData, nint.Zero);
            if (handled) return result;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void EnsureNativeClass()
    {
        if (Interlocked.Exchange(ref _classRegistered, 1) != 0) return;
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CsOwnDc,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(NativeWindowProc),
            hInstance = ModuleHandle,
            hCursor = LoadCursor(nint.Zero, new nint(32512)),
            lpszClassName = NativeClassName
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                Interlocked.Exchange(ref _classRegistered, 0);
                throw new Win32Exception(error, "The native embedded-client host class could not be registered.");
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HostWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public int style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public nint lpCreateParams;
        public nint hInstance;
        public nint hMenu;
        public nint hwndParent;
        public int cy;
        public int cx;
        public int y;
        public int x;
        public int style;
        public nint lpszName;
        public nint lpszClass;
        public uint dwExStyle;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

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
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint LoadCursor(nint instance, nint cursor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
}

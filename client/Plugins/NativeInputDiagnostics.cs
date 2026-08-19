using System.Runtime.InteropServices;
using System.Text;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Read-only diagnostics for physical input delivered over the docked client
/// viewport. It observes the system's last-input tick and never installs a
/// hook, suppresses input, or sends a native message.
/// </summary>
internal sealed class NativeInputDiagnostics
{
    private uint _lastInputTick;

    public string? CaptureAfterSystemInput(nint viewport, nint clientRoot)
    {
        if (viewport == nint.Zero || clientRoot == nint.Zero ||
            !IsWindow(viewport) || !IsWindow(clientRoot)) return null;

        var input = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref input) || input.dwTime == _lastInputTick) return null;
        _lastInputTick = input.dwTime;
        if (!GetCursorPos(out var cursor) || !GetWindowRect(viewport, out var viewportRect) ||
            cursor.X < viewportRect.Left || cursor.X >= viewportRect.Right ||
            cursor.Y < viewportRect.Top || cursor.Y >= viewportRect.Bottom) return null;

        var hit = WindowFromPoint(cursor);
        var foreground = GetForegroundWindow();
        var rootThread = GetWindowThreadProcessId(clientRoot, out var rootPid);
        var foregroundThread = GetWindowThreadProcessId(foreground, out var foregroundPid);
        var hitThread = GetWindowThreadProcessId(hit, out var hitPid);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        _ = rootThread != 0 && GetGUIThreadInfo(rootThread, ref info);
        _ = GetWindowRect(clientRoot, out var rootRect);

        return $"Last system-input observation (physical/injected not distinguishable): cursor={cursor.X},{cursor.Y}; " +
               $"hit={Describe(hit, hitPid, hitThread)}; " +
               $"foreground={Describe(foreground, foregroundPid, foregroundThread)}; " +
               $"client=0x{clientRoot.ToInt64():X}/root=0x{GetAncestor(clientRoot, GaRoot).ToInt64():X}/" +
               $"parent=0x{GetParent(clientRoot).ToInt64():X}/owner=0x{GetWindow(clientRoot, GwOwner).ToInt64():X}/" +
               $"pid={rootPid}/tid={rootThread}/active=0x{info.hwndActive.ToInt64():X}/" +
               $"focus=0x{info.hwndFocus.ToInt64():X}/capture=0x{info.hwndCapture.ToInt64():X}/" +
               $"style=0x{GetWindowLongPtr(clientRoot, GwlStyle).ToInt64():X}/" +
               $"ex=0x{GetWindowLongPtr(clientRoot, GwlExStyle).ToInt64():X}/" +
               $"dpi={GetDpiForWindow(clientRoot)}/integrity={ProcessIntegrity.ForWindow(clientRoot)}/" +
               $"bounds={rootRect.Left},{rootRect.Top},{rootRect.Right},{rootRect.Bottom}.";
    }

    private static string Describe(nint window, uint processId, uint threadId)
    {
        if (window == nint.Zero) return "none";
        var className = new StringBuilder(128);
        _ = GetClassName(window, className, className.Capacity);
        return $"0x{window.ToInt64():X}/{className}/pid={processId}/tid={threadId}";
    }

    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
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

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder className, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}

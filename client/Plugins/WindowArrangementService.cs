using System.Runtime.InteropServices;
using System.Diagnostics;

namespace RobloxAltClient.Plugins;

public sealed class WindowArrangementService
{
    private readonly Dictionary<WindowIdentity, WindowPlacement> _original = [];

    public IReadOnlyList<string> Stack(IReadOnlyList<ManagedAccountSnapshot> accounts, nint monitor = 0)
    {
        var target = ResolveWorkArea(accounts, monitor);
        var errors = new List<string>();
        foreach (var account in accounts)
        {
            if (!TryResolveWindow(account, out var window) || !TrySnapshot(window, out var placement))
            {
                errors.Add($"{account.Label}: window unavailable or embedded in RAM");
                continue;
            }
            _original.TryAdd(new WindowIdentity(window), placement);
            if (!TryMoveNormal(window.WindowHandle, target.Left, target.Top, target.Width, target.Height, out var error))
            {
                errors.Add($"{account.Label}: {error}");
            }
        }
        return errors;
    }

    // With an explicit monitor GRID stays on that work area; otherwise it
    // spreads the windows across every monitor's work area.
    public IReadOnlyList<string> Grid(IReadOnlyList<ManagedAccountSnapshot> accounts, nint monitor = 0)
    {
        var errors = new List<string>();
        if (accounts.Count == 0) return errors;
        IReadOnlyList<LayoutRect> workAreas = monitor != nint.Zero && TryGetMonitorInfo(monitor, out var requested)
            ? [ToLayoutRect(requested.rcWork)]
            : EnumerateWorkAreas();
        if (workAreas.Count == 0) workAreas = [ToLayoutRect(ResolveWorkArea(accounts, monitor))];
        var cells = GridLayout.Compute(workAreas, accounts.Count);
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            if (!TryResolveWindow(account, out var window) || !TrySnapshot(window, out var placement))
            {
                errors.Add($"{account.Label}: window unavailable or embedded in RAM");
                continue;
            }
            _original.TryAdd(new WindowIdentity(window), placement);
            var cell = cells[index];
            if (!TryMoveNormal(window.WindowHandle, cell.Left, cell.Top, cell.Width, cell.Height, out var error))
            {
                errors.Add($"{account.Label}: {error}");
            }
        }
        return errors;
    }

    public IReadOnlyList<string> Reset(IReadOnlyList<ManagedAccountSnapshot> accounts)
    {
        var errors = new List<string>();
        foreach (var account in accounts)
        {
            if (!TryResolveWindow(account, out var window))
            {
                errors.Add($"{account.Label}: window unavailable or embedded in RAM");
                continue;
            }
            if (!_original.TryGetValue(new WindowIdentity(window), out var placement)) continue;
            if (!ValidateIdentity(window)) { errors.Add($"{account.Label}: window identity changed"); continue; }
            if (!TryRestore(window.WindowHandle, placement, out var error))
            {
                errors.Add($"{account.Label}: {error}");
            }
        }
        if (errors.Count == 0)
        {
            foreach (var account in accounts) _original.Remove(new WindowIdentity(account with { WindowHandle = RootWindow(account.WindowHandle) }));
        }
        return errors;
    }

    private static RECT ResolveWorkArea(IReadOnlyList<ManagedAccountSnapshot> accounts, nint monitor)
    {
        if (monitor != nint.Zero && TryGetMonitorInfo(monitor, out var requested)) return requested.rcWork;
        var hwnd = RootWindow(accounts.FirstOrDefault()?.WindowHandle ?? nint.Zero);
        var selected = hwnd == nint.Zero ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTONEAREST) : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return TryGetMonitorInfo(selected, out var info) ? info.rcWork : new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private static bool TryGetMonitorInfo(nint monitor, out MONITORINFO info)
    {
        info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return monitor != nint.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static bool TrySnapshot(ManagedAccountSnapshot account, out WindowPlacement placement)
    {
        placement = default;
        var hwnd = account.WindowHandle;
        if (!ValidateIdentity(account)) return false;
        var native = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        // GetWindowRect reports the parked icon position for a minimized window,
        // so the restorable bounds come from the placement record instead.
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect) || !GetWindowPlacement(hwnd, ref native)) return false;
        placement = new WindowPlacement(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            IsIconic(hwnd), IsZoomed(hwnd), native);
        return true;
    }

    // Arrangement needs a normal window: SetWindowPos on a maximized window keeps
    // WS_MAXIMIZE, and on a minimized one only moves the parked icon.
    // SW_SHOWNOACTIVATE restores in place without activating the client.
    private static bool TryMoveNormal(nint hwnd, int x, int y, int width, int height, out string error)
    {
        error = string.Empty;
        if (IsIconic(hwnd) || IsZoomed(hwnd))
        {
            var current = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref current)) { error = Marshal.GetLastWin32Error().ToString(); return false; }
            current.flags = 0;
            current.showCmd = SW_SHOWNOACTIVATE;
            if (!SetWindowPlacement(hwnd, ref current)) { error = Marshal.GetLastWin32Error().ToString(); return false; }
        }
        if (!SetWindowPos(hwnd, nint.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER))
        {
            error = Marshal.GetLastWin32Error().ToString();
            return false;
        }
        return true;
    }

    private static bool TryRestore(nint hwnd, WindowPlacement original, out string error)
    {
        error = string.Empty;
        var native = original.Native;
        native.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        if (original.IsMinimized)
        {
            // Keeps WPF_RESTORETOMAXIMIZED so un-minimizing later returns to the
            // state the user had before arranging.
            native.showCmd = SW_SHOWMINNOACTIVE;
            if (SetWindowPlacement(hwnd, ref native)) return true;
            error = Marshal.GetLastWin32Error().ToString();
            return false;
        }

        // Put the normal (restore) rectangle back first so the client's own
        // restore button returns to where it was before arranging.
        native.flags = 0;
        native.showCmd = SW_SHOWNOACTIVATE;
        if (!SetWindowPlacement(hwnd, ref native)) { error = Marshal.GetLastWin32Error().ToString(); return false; }

        var flags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER;
        if (original.IsMaximized)
        {
            // Every ShowWindow maximize command activates the window, so the
            // maximized state is reapplied as the WS_MAXIMIZE style plus the
            // captured maximized bounds, committed with SWP_NOACTIVATE.
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            SetLastError(0);
            if (SetWindowLongPtr(hwnd, GWL_STYLE, new nint(style | WS_MAXIMIZE)) == nint.Zero && Marshal.GetLastWin32Error() != 0)
            {
                error = Marshal.GetLastWin32Error().ToString();
                return false;
            }
            flags |= SWP_FRAMECHANGED;
        }
        if (!SetWindowPos(hwnd, nint.Zero, original.Left, original.Top, original.Width, original.Height, flags))
        {
            error = Marshal.GetLastWin32Error().ToString();
            return false;
        }
        return true;
    }

    private static IReadOnlyList<LayoutRect> EnumerateWorkAreas()
    {
        var areas = new List<LayoutRect>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            if (TryGetMonitorInfo(monitor, out var info)) areas.Add(ToLayoutRect(info.rcWork));
            return true;
        };
        _ = EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback);
        return areas;
    }

    private static LayoutRect ToLayoutRect(RECT rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    private static bool TryResolveWindow(ManagedAccountSnapshot account, out ManagedAccountSnapshot window)
    {
        window = account with { WindowHandle = RootWindow(account.WindowHandle) };
        if (window.WindowHandle == nint.Zero || !IsWindow(window.WindowHandle)) return false;
        GetWindowThreadProcessId(window.WindowHandle, out var ownerPid);
        // An embedded render HWND climbs to RAM's top-level ancestor. Never
        // pass that ancestor to the arrangement APIs or RAM itself could move.
        return ownerPid == window.ProcessId;
    }

    private static nint RootWindow(nint hwnd) => hwnd == nint.Zero ? nint.Zero : GetAncestor(hwnd, GA_ROOT);

    private static bool ValidateIdentity(ManagedAccountSnapshot account)
    {
        if (account.WindowHandle == nint.Zero || !IsWindow(account.WindowHandle)) return false;
        GetWindowThreadProcessId(account.WindowHandle, out var pid);
        if (pid != account.ProcessId) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == account.ProcessStartTimeUtcTicks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private readonly record struct WindowIdentity(nint Hwnd, int ProcessId, long StartTicks)
    { public WindowIdentity(ManagedAccountSnapshot snapshot) : this(snapshot.WindowHandle, snapshot.ProcessId, snapshot.ProcessStartTimeUtcTicks) { } }
    private readonly record struct WindowPlacement(int Left, int Top, int Width, int Height, bool IsMinimized, bool IsMaximized, WINDOWPLACEMENT Native);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const int SW_SHOWMINNOACTIVE = 7;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZE = 0x01000000L;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    private struct POINT { public int X; public int Y; }
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private struct WINDOWPLACEMENT { public int length; public int flags; public int showCmd; public POINT ptMinPosition; public POINT ptMaxPosition; public RECT rcNormalPosition; }
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);
}

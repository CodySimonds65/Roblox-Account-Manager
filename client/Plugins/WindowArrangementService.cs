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
            if (!SetWindowPos(window.WindowHandle, nint.Zero, target.Left, target.Top, target.Width, target.Height,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER))
            {
                errors.Add($"{account.Label}: {Marshal.GetLastWin32Error()}");
            }
        }
        return errors;
    }

    public IReadOnlyList<string> Grid(IReadOnlyList<ManagedAccountSnapshot> accounts, nint monitor = 0)
    {
        var work = ResolveWorkArea(accounts, monitor);
        var errors = new List<string>();
        if (accounts.Count == 0) return errors;
        var columns = (int)Math.Ceiling(Math.Sqrt(accounts.Count));
        var rows = (int)Math.Ceiling(accounts.Count / (double)columns);
        var width = Math.Max(1, work.Width / columns);
        var height = Math.Max(1, work.Height / rows);
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            if (!TryResolveWindow(account, out var window) || !TrySnapshot(window, out var placement))
            {
                errors.Add($"{account.Label}: window unavailable or embedded in RAM");
                continue;
            }
            _original.TryAdd(new WindowIdentity(window), placement);
            var x = work.Left + (index % columns) * width;
            var y = work.Top + (index / columns) * height;
            if (!SetWindowPos(window.WindowHandle, nint.Zero, x, y, Math.Max(160, width), Math.Max(120, height),
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER))
            {
                errors.Add($"{account.Label}: {Marshal.GetLastWin32Error()}");
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
            if (!SetWindowPos(window.WindowHandle, nint.Zero, placement.Left, placement.Top, placement.Width, placement.Height,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER))
            {
                errors.Add($"{account.Label}: {Marshal.GetLastWin32Error()}");
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
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect)) return false;
        placement = new WindowPlacement(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, account.IsMinimized, IsZoomed(hwnd));
        return true;
    }

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
    private readonly record struct WindowPlacement(int Left, int Top, int Width, int Height, bool IsMinimized, bool IsMaximized);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    private struct POINT { public int X; public int Y; }
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
}

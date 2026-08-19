using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Injects input into an EMBEDDED (child) game window via SendInput, which
/// produces real hardware-level input that raw-input consumers (Roblox) accept —
/// unlike posted window messages. The client must be embedded in and focused by
/// the launcher so no desktop focus is stolen.
/// </summary>
public sealed class InputSendInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint KeyeventfScanCode = 0x0008;
    private const uint MouseeventfLeftDown = 0x0002;
    private const uint MouseeventfLeftUp = 0x0004;
    private const uint MouseeventfRightDown = 0x0008;
    private const uint MouseeventfRightUp = 0x0010;
    private const uint MouseeventfMiddleDown = 0x0020;
    private const uint MouseeventfMiddleUp = 0x0040;
    private const uint MouseeventfWheel = 0x0800;

    public async Task<BackgroundInputResult> PostAsync(
        nint rootWindow,
        IReadOnlyList<PluginInputEvent> events,
        CancellationToken cancellationToken,
        Func<bool>? targetValidator = null)
    {
        if (rootWindow == nint.Zero || events.Count == 0 || !IsWindow(rootWindow))
        {
            return BackgroundInputResult.Failure("unavailable", "The embedded client window is unavailable.", GetForegroundWindow(), GetForegroundWindow());
        }
        var foregroundBefore = GetForegroundWindow();
        if (!IsSafeTarget(rootWindow, targetValidator))
        {
            return BackgroundInputResult.Failure("focus-lost", "The embedded client is not the visible foreground target.", foregroundBefore, GetForegroundWindow());
        }
        var posted = 0;
        long previousOffset = 0;
        foreach (var input in events.OrderBy(item => item.OffsetMicroseconds))
        {
            var gapMicroseconds = input.OffsetMicroseconds - previousOffset;
            if (gapMicroseconds > 0)
            {
                await Task.Delay(TimeSpan.FromTicks(gapMicroseconds * 10), cancellationToken).ConfigureAwait(false);
            }
            previousOffset = input.OffsetMicroseconds;
            cancellationToken.ThrowIfCancellationRequested();
            // Revalidate before every event. HWND values can be reused, tabs can
            // switch, and the user can move to another application during a macro.
            if (!IsSafeTarget(rootWindow, targetValidator))
            {
                var after = GetForegroundWindow();
                return BackgroundInputResult.Failure("focus-lost", "The client lost focus during playback; input was stopped.", foregroundBefore, after, posted);
            }
            if (!TryInject(rootWindow, input, out var error))
            {
                var after = GetForegroundWindow();
                return BackgroundInputResult.Failure(error.Code, error.Message, foregroundBefore, after, posted);
            }
            posted++;
        }
        var foregroundAfter = GetForegroundWindow();
        return new BackgroundInputResult(true, foregroundBefore == foregroundAfter ? "ok" : "foreground-changed",
            foregroundBefore == foregroundAfter ? "All input was injected." : "Input injected; foreground changed externally.",
            posted, foregroundBefore, foregroundAfter);
    }

    private static bool IsSafeTarget(nint rootWindow, Func<bool>? targetValidator)
    {
        if (!IsWindow(rootWindow) || !IsWindowVisible(rootWindow) ||
            GetForegroundWindow() != GetAncestor(rootWindow, GaRoot)) return false;
        try
        {
            return targetValidator?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInject(nint rootWindow, PluginInputEvent input, out (string Code, string Message) error)
    {
        error = default;
        switch (input.Kind)
        {
            case PluginInputKind.KeyDown or PluginInputKind.KeyUp:
                {
                    var keyUp = input.Kind == PluginInputKind.KeyUp;
                    var keyboard = new KEYBDINPUT
                    {
                        Vk = 0,
                        Scan = (ushort)Math.Clamp(input.ScanCode, 0, 255),
                        Flags = KeyboardFlags(keyUp, input.Extended)
                    };
                    return Inject(new INPUT { Type = InputKeyboard, Data = new INPUTUNION { Keyboard = keyboard } }, out error);
                }
            case PluginInputKind.MouseButtonDown or PluginInputKind.MouseButtonUp:
                {
                    if (!TryGetScreenPoint(rootWindow, input, out var x, out var y)) { error = ("unavailable", "The embedded client has no client area."); return false; }
                    if (!SetCursorPos(x, y)) { error = ("post-failed", "The cursor could not be positioned."); return false; }
                    var down = input.Kind == PluginInputKind.MouseButtonDown;
                    var flags = ButtonFlag(input.Button, down);
                    if (flags == 0) { error = ("unsupported-input", "The mouse button is not supported."); return false; }
                    return Inject(new INPUT { Type = InputMouse, Data = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = flags } } }, out error);
                }
            case PluginInputKind.MouseWheel:
                {
                    if (!TryGetScreenPoint(rootWindow, input, out var x, out var y)) { error = ("unavailable", "The embedded client has no client area."); return false; }
                    if (!SetCursorPos(x, y)) { error = ("post-failed", "The cursor could not be positioned."); return false; }
                    var mouse = new MOUSEINPUT { Flags = MouseeventfWheel, MouseData = WheelData(input.WheelDelta) };
                    return Inject(new INPUT { Type = InputMouse, Data = new INPUTUNION { Mouse = mouse } }, out error);
                }
            case PluginInputKind.MouseMove:
                {
                    if (!TryGetScreenPoint(rootWindow, input, out var x, out var y)) { error = ("unavailable", "The embedded client has no client area."); return false; }
                    if (!SetCursorPos(x, y)) { error = ("post-failed", "The cursor could not be positioned."); return false; }
                    return true;
                }
            default:
                error = ("unsupported-input", "The input event kind is not supported.");
                return false;
        }
    }

    private static bool Inject(INPUT input, out (string Code, string Message) error)
    {
        error = default;
        var injected = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (injected == 0)
        {
            error = ("post-failed", $"SendInput failed (Win32 error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        return true;
    }

    internal static uint KeyboardFlags(bool keyUp, bool extended) =>
        KeyeventfScanCode | (extended ? KeyeventfExtendedKey : 0) | (keyUp ? KeyeventfKeyUp : 0);

    internal static uint ButtonFlag(int button, bool down) => button switch
    {
        0 => down ? MouseeventfLeftDown : MouseeventfLeftUp,
        1 => down ? MouseeventfRightDown : MouseeventfRightUp,
        2 => down ? MouseeventfMiddleDown : MouseeventfMiddleUp,
        _ => 0
    };

    internal static uint WheelData(int wheelDelta) => unchecked((uint)(short)Math.Clamp(wheelDelta, short.MinValue, short.MaxValue));

    private static bool TryGetScreenPoint(nint rootWindow, PluginInputEvent input, out int x, out int y)
    {
        x = y = 0;
        if (!GetClientRect(rootWindow, out var rect)) return false;
        var origin = new POINT();
        if (!ClientToScreen(rootWindow, ref origin)) return false;
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        x = origin.X + Math.Clamp((int)Math.Round(input.NormalizedX * (width - 1)), 0, width - 1);
        y = origin.Y + Math.Clamp((int)Math.Round(input.NormalizedY * (height - 1)), 0, height - 1);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int DX; public int DY; public uint MouseData; public uint Flags; public uint Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public nint ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref POINT point);
    private const uint GaRoot = 2;
}

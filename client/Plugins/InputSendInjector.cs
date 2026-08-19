using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Injects macro input only when the selected docked Roblox top-level is the
/// actual desktop foreground target. Human input never passes through here.
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
    private const uint MouseeventfMove = 0x0001;
    private const uint MouseeventfVirtualDesk = 0x4000;
    private const uint MouseeventfAbsolute = 0x8000;

    public async Task<BackgroundInputResult> PostAsync(
        nint rootWindow,
        IReadOnlyList<PluginInputEvent> events,
        CancellationToken cancellationToken,
        Func<bool>? targetValidator = null,
        Func<IReadOnlyList<PluginInputEvent>, Task>? releaseFallback = null,
        InputDeliveryIntent deliveryIntent = InputDeliveryIntent.Default,
        string? traceId = null)
    {
        _ = deliveryIntent; // Legacy delivery intents are rejected before reaching this injector.
        if (rootWindow == nint.Zero || events.Count == 0 || !IsWindow(rootWindow))
        {
            return Stamp(BackgroundInputResult.Failure("unavailable", "The docked client window is unavailable.", GetForegroundWindow(), GetForegroundWindow()), rootWindow, events.Count, traceId);
        }
        var foregroundBefore = GetForegroundWindow();
        if (!IsSafeTarget(rootWindow, targetValidator))
        {
            return Stamp(BackgroundInputResult.Failure("focus-lost", "The embedded client is not the visible foreground target.", foregroundBefore, GetForegroundWindow()), rootWindow, events.Count, traceId);
        }
        var posted = 0;
        long previousOffset = 0;
        var postedEvents = new List<PluginInputEvent>();
        try
        {
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
                    var released = await ReleaseHeldInputsAsync(rootWindow, postedEvents, targetValidator, releaseFallback).ConfigureAwait(false);
                    var after = GetForegroundWindow();
                    return Stamp(BackgroundInputResult.Failure("focus-lost", released
                        ? "The client lost focus during playback; input was stopped and held inputs were released."
                        : "The client lost focus during playback; input was stopped, but held-input cleanup was unsafe after takeover.",
                        foregroundBefore, after, posted) with
                    {
                        Verification = released ? "guarded" : "cleanup-unavailable"
                    }, rootWindow, events.Count, traceId);
                }
                if (!TryInject(rootWindow, input, out var error))
                {
                    var released = await ReleaseHeldInputsAsync(rootWindow, postedEvents, targetValidator, releaseFallback).ConfigureAwait(false);
                    var after = GetForegroundWindow();
                    return Stamp(BackgroundInputResult.Failure(error.Code, released ? error.Message : $"{error.Message} Held-input cleanup was unsafe.", foregroundBefore, after, posted) with
                    {
                        Verification = released ? "guarded" : "cleanup-unavailable"
                    }, rootWindow, events.Count, traceId);
                }
                postedEvents.Add(input);
                posted++;
            }
        }
        catch (OperationCanceledException)
        {
            await ReleaseHeldInputsAsync(rootWindow, postedEvents, targetValidator, releaseFallback).ConfigureAwait(false);
            throw;
        }

        await ReleaseHeldInputsAsync(rootWindow, postedEvents, targetValidator, releaseFallback).ConfigureAwait(false);
        var foregroundAfter = GetForegroundWindow();
        return Stamp(new BackgroundInputResult(true, foregroundBefore == foregroundAfter ? "ok" : "foreground-changed",
            foregroundBefore == foregroundAfter ? "All input was injected." : "Input injected; foreground changed externally.",
            posted, foregroundBefore, foregroundAfter)
        {
            DeliveryMode = "send-input",
            Verification = "guarded"
        }, rootWindow, events.Count, traceId);
    }

    private static BackgroundInputResult Stamp(BackgroundInputResult result, nint rootWindow,
        int requestedCount, string? traceId) => result with
        {
            TraceId = traceId,
            RequestedCount = requestedCount,
            TargetRootWindow = rootWindow,
            TargetRenderWindow = nint.Zero
        };

    private static async Task<bool> ReleaseHeldInputsAsync(
        nint rootWindow,
        IReadOnlyList<PluginInputEvent> postedEvents,
        Func<bool>? targetValidator,
        Func<IReadOnlyList<PluginInputEvent>, Task>? releaseFallback)
    {
        var releases = PendingReleases(postedEvents);
        return await ReleasePendingInputsAsync(
            releases,
            IsReleaseSafe(rootWindow),
            release => TryInject(rootWindow, release, out _),
            releaseFallback).ConfigureAwait(false);
    }

    // Releasing events is safe only while the exact validated root is still
    // foreground. It intentionally does not invoke the full target validator:
    // identity/focus drift must stop new events, but a still-foreground root can
    // safely receive the key/button-up cleanup before it is hidden or destroyed.
    private static bool IsReleaseSafe(nint rootWindow) =>
        IsWindow(rootWindow) && IsWindowVisible(rootWindow) &&
        GetAncestor(rootWindow, GaRoot) == rootWindow && GetForegroundWindow() == rootWindow;

    internal static async Task<bool> ReleasePendingInputsAsync(
        IReadOnlyList<PluginInputEvent> releases,
        bool realTargetSafe,
        Func<PluginInputEvent, bool> realRelease,
        Func<IReadOnlyList<PluginInputEvent>, Task>? releaseFallback)
    {
        if (releases.Count == 0) return true;
        if (realTargetSafe)
        {
            var released = true;
            foreach (var release in releases)
                released &= realRelease(release);
            if (released) return true;
        }
        if (releaseFallback is not null)
        {
            try
            {
                await releaseFallback(releases).ConfigureAwait(false);
                return true;
            }
            catch { /* Message-level cleanup is best effort and never targets a new foreground window. */ }
        }
        return false;
    }

    internal static IReadOnlyList<PluginInputEvent> PendingReleases(IReadOnlyList<PluginInputEvent> postedEvents)
    {
        var keys = new Dictionary<(int VirtualKey, int ScanCode, bool Extended), PluginInputEvent>();
        var buttons = new Dictionary<int, PluginInputEvent>();
        foreach (var input in postedEvents)
        {
            var key = (input.VirtualKey, input.ScanCode, input.Extended);
            switch (input.Kind)
            {
                case PluginInputKind.KeyDown:
                    keys[key] = input;
                    break;
                case PluginInputKind.KeyUp:
                    keys.Remove(key);
                    break;
                case PluginInputKind.MouseButtonDown:
                    buttons[input.Button] = input;
                    break;
                case PluginInputKind.MouseButtonUp:
                    buttons.Remove(input.Button);
                    break;
            }
        }
        return keys.Values.Select(input => input with { Kind = PluginInputKind.KeyUp, OffsetMicroseconds = 0 })
            .Concat(buttons.Values.Select(input => input with { Kind = PluginInputKind.MouseButtonUp, OffsetMicroseconds = 0 }))
            .ToArray();
    }

    private static bool IsSafeTarget(nint rootWindow, Func<bool>? targetValidator)
    {
        if (!IsWindow(rootWindow) || !IsWindowVisible(rootWindow) ||
            GetAncestor(rootWindow, GaRoot) != rootWindow || GetForegroundWindow() != rootWindow) return false;
        var gameThread = GetWindowThreadProcessId(rootWindow, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (gameThread == 0 || !GetGUIThreadInfo(gameThread, ref info) ||
            !IsFocusWithin(rootWindow, info.hwndFocus)) return false;
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
                    if (!TryCreateAbsoluteMove(rootWindow, input, out var move)) { error = ("unavailable", "The docked client has no client area."); return false; }
                    var down = input.Kind == PluginInputKind.MouseButtonDown;
                    var flags = ButtonFlag(input.Button, down);
                    if (flags == 0) { error = ("unsupported-input", "The mouse button is not supported."); return false; }
                    return Inject([
                        move,
                        new INPUT { Type = InputMouse, Data = new INPUTUNION { Mouse = new MOUSEINPUT { Flags = flags } } }
                    ], out error);
                }
            case PluginInputKind.MouseWheel:
                {
                    if (!TryCreateAbsoluteMove(rootWindow, input, out var move)) { error = ("unavailable", "The docked client has no client area."); return false; }
                    var mouse = new MOUSEINPUT { Flags = MouseeventfWheel, MouseData = WheelData(input.WheelDelta) };
                    return Inject([move, new INPUT { Type = InputMouse, Data = new INPUTUNION { Mouse = mouse } }], out error);
                }
            case PluginInputKind.MouseMove:
                {
                    if (!TryCreateAbsoluteMove(rootWindow, input, out var move)) { error = ("unavailable", "The docked client has no client area."); return false; }
                    return Inject([move], out error);
                }
            default:
                error = ("unsupported-input", "The input event kind is not supported.");
                return false;
        }
    }

    private static bool Inject(INPUT input, out (string Code, string Message) error) => Inject([input], out error);

    private static bool Inject(INPUT[] inputs, out (string Code, string Message) error)
    {
        error = default;
        var injected = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (injected != inputs.Length)
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

    internal static (int X, int Y) NormalizeAbsolutePoint(int screenX, int screenY, int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
    {
        var width = Math.Max(2, virtualWidth);
        var height = Math.Max(2, virtualHeight);
        var x = Math.Clamp(screenX - virtualLeft, 0, width - 1);
        var y = Math.Clamp(screenY - virtualTop, 0, height - 1);
        return (
            (int)Math.Round(x * 65535d / (width - 1)),
            (int)Math.Round(y * 65535d / (height - 1)));
    }

    private static bool TryCreateAbsoluteMove(nint rootWindow, PluginInputEvent input, out INPUT move)
    {
        move = default;
        if (!TryGetScreenPoint(rootWindow, input, out var screenX, out var screenY)) return false;
        var (x, y) = NormalizeAbsolutePoint(
            screenX,
            screenY,
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
        move = new INPUT
        {
            Type = InputMouse,
            Data = new INPUTUNION
            {
                Mouse = new MOUSEINPUT
                {
                    DX = x,
                    DY = y,
                    Flags = MouseeventfMove | MouseeventfAbsolute | MouseeventfVirtualDesk
                }
            }
        };
        return true;
    }

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
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref POINT point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static bool IsFocusWithin(nint root, nint focusedWindow) =>
        focusedWindow != nint.Zero && (focusedWindow == root || IsChild(root, focusedWindow));

    private const uint GaRoot = 2;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
}

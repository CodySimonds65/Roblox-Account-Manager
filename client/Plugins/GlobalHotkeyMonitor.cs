using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Observes the global keyboard input stream from the elevated launcher, so
/// plugins (medium integrity) can still act on hotkeys while an elevated
/// foreground window (the game client) has focus — UIPI hides those keys
/// from medium-integrity low-level hooks.
/// </summary>
public sealed class GlobalHotkeyMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlkhfLowerIlInjected = 0x00000002;
    private const uint LlkhfUp = 0x00000080;

    private readonly LowLevelKeyboardProc _keyboardProc;
    private nint _keyboardHook;

    public event EventHandler<int>? KeyDown;
    public event EventHandler<int>? KeyUp;

    public GlobalHotkeyMonitor()
    {
        _keyboardProc = KeyboardHook;
    }

    public void Start()
    {
        if (_keyboardHook != nint.Zero) return;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookExW(WhKeyboardLl, _keyboardProc, module, 0);
        if (_keyboardHook == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Could not start the global hotkey monitor (Win32 error {error}).");
        }
    }

    public void Stop()
    {
        if (_keyboardHook != nint.Zero) UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = nint.Zero;
    }

    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != nint.Zero)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (data.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) != 0;
            if (!injected)
            {
                var vk = unchecked((int)data.VirtualKeyCode);
                try
                {
                    if ((data.Flags & LlkhfUp) != 0) KeyUp?.Invoke(this, vk);
                    else KeyDown?.Invoke(this, vk);
                }
                catch
                {
                    // A failing handler must never break the input hook chain.
                }
            }
        }
        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint VirtualKeyCode; public uint ScanCode; public uint Flags; public uint Time; public nint ExtraInfo; }
    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc callback, nint moduleHandle, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
}

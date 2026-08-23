using System.Runtime.InteropServices;
using System.Text;
using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

public readonly record struct MacWindowFrame(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width >= 400 && Height >= 300;
}

public sealed record MacAccessibleWindow(
    string Identifier,
    string? Title,
    MacWindowFrame Frame,
    bool IsMinimized,
    bool IsFullScreen);

public interface IMacAccessibilityApi
{
    MacCapabilityResult GetCapability();
    MacAccessibleWindow? FindMainWindow(Contracts.RobloxProcessIdentity process);
    bool TrySetFrame(Contracts.RobloxProcessIdentity process, string windowIdentifier, MacWindowFrame frame);
    bool TrySetMinimized(Contracts.RobloxProcessIdentity process, string windowIdentifier, bool minimized);
    bool TryRaise(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        Func<bool> canRaise);
    void ForgetWindow(Contracts.RobloxProcessIdentity process, string windowIdentifier);
}

/// <summary>
/// Public Accessibility API adapter. Roblox remains a separate top-level window;
/// this adapter never reparents it and never creates or forwards input events.
/// </summary>
public sealed class MacAccessibilityApi : IMacAccessibilityApi
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;
    private const int AxValueCgPoint = 1;
    private const int AxValueCgSize = 2;
    private static readonly object NativeLibraryGate = new();
    private static nint _coreFoundationHandle;
    private readonly IRobloxProcessLocator _processLocator;
    private readonly object _windowsGate = new();
    private readonly Dictionary<string, RetainedWindow> _retainedWindows = new(StringComparer.Ordinal);

    public MacAccessibilityApi(IRobloxProcessLocator? processLocator = null)
    {
        _processLocator = processLocator ?? new MacRobloxProcessLocator();
    }

    public MacCapabilityResult GetCapability()
    {
        if (!OperatingSystem.IsMacOS())
            return MacCapabilityResult.PlatformNotSupported("Accessibility window management is only available on macOS.");
        return NativeMethods.AXIsProcessTrusted()
            ? MacCapabilityResult.Supported()
            : MacCapabilityResult.PermissionRequired(
                "Grant Accessibility permission in System Settings > Privacy & Security > Accessibility.");
    }

    public MacAccessibleWindow? FindMainWindow(Contracts.RobloxProcessIdentity process)
    {
        if (!GetCapability().IsSupported || !IsCurrentManagedIdentity(process)) return null;
        return WithApplication(process.Pid, application =>
        {
            var candidates = EnumerateWindows(application);
            try
            {
                var selected = SelectWindow(candidates);
                return selected is not null && IsCurrentManagedIdentity(process)
                    ? selected.Snapshot with { Identifier = GetOrCreateWindowIdentifier(process, selected.Element) }
                    : null;
            }
            finally { ReleaseCandidates(candidates); }
        });
    }

    public bool TrySetFrame(Contracts.RobloxProcessIdentity process, string windowIdentifier, MacWindowFrame frame)
    {
        if (!frame.IsValid || string.IsNullOrWhiteSpace(windowIdentifier)) return false;
        return WithSelectedWindow(process, windowIdentifier, window =>
        {
            var position = new CGPoint(frame.Left, frame.Top);
            var size = new CGSize(frame.Width, frame.Height);
            var positionValue = NativeMethods.AXValueCreate(AxValueCgPoint, ref position);
            var sizeValue = NativeMethods.AXValueCreate(AxValueCgSize, ref size);
            if (positionValue == nint.Zero || sizeValue == nint.Zero)
            {
                Release(positionValue);
                Release(sizeValue);
                return false;
            }

            try
            {
                return SetAttribute(window, "AXPosition", positionValue)
                    && SetAttribute(window, "AXSize", sizeValue);
            }
            finally
            {
                Release(positionValue);
                Release(sizeValue);
            }
        });
    }

    public bool TrySetMinimized(Contracts.RobloxProcessIdentity process, string windowIdentifier, bool minimized)
    {
        if (string.IsNullOrWhiteSpace(windowIdentifier)) return false;
        return WithSelectedWindow(process, windowIdentifier,
            window => SetAttribute(window, "AXMinimized", GetBoolean(minimized)));
    }

    public bool TryRaise(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        Func<bool> canRaise)
    {
        if (string.IsNullOrWhiteSpace(windowIdentifier)) return false;
        return WithSelectedWindow(process, windowIdentifier, window =>
        {
            if (!canRaise()) return false;
            var action = CreateString("AXRaise");
            if (action == nint.Zero) return false;
            try { return NativeMethods.AXUIElementPerformAction(window, action) == 0; }
            finally { Release(action); }
        });
    }

    public void ForgetWindow(Contracts.RobloxProcessIdentity process, string windowIdentifier)
    {
        nint element = nint.Zero;
        lock (_windowsGate)
        {
            if (_retainedWindows.TryGetValue(windowIdentifier, out var retained)
                && retained.Process.Matches(process))
            {
                element = retained.Element;
                _retainedWindows.Remove(windowIdentifier);
            }
        }
        Release(element);
    }

    private bool WithSelectedWindow(
        Contracts.RobloxProcessIdentity process,
        string identifier,
        Func<nint, bool> action)
    {
        if (!OperatingSystem.IsMacOS() || !IsCurrentManagedIdentity(process)) return false;
        RetainedWindow retained;
        lock (_windowsGate)
        {
            if (!_retainedWindows.TryGetValue(identifier, out retained!)
                || !retained.Process.Matches(process)) return false;
        }
        return WithApplication(process.Pid, application =>
        {
            var candidates = EnumerateWindows(application);
            try
            {
                var matches = candidates.Where(candidate =>
                    NativeMethods.CFEqual(candidate.Element, retained.Element)).ToArray();
                return matches.Length == 1
                    && IsCurrentManagedIdentity(process)
                    && action(matches[0].Element);
            }
            finally { ReleaseCandidates(candidates); }
        });
    }

    private string GetOrCreateWindowIdentifier(Contracts.RobloxProcessIdentity process, nint element)
    {
        lock (_windowsGate)
        {
            var existing = _retainedWindows.FirstOrDefault(pair =>
                pair.Value.Process.Matches(process)
                && NativeMethods.CFEqual(pair.Value.Element, element));
            if (!string.IsNullOrWhiteSpace(existing.Key)) return existing.Key;

            var identifier = $"ax-window-{Guid.NewGuid():N}";
            _ = NativeMethods.CFRetain(element);
            _retainedWindows.Add(identifier, new RetainedWindow(process, element));
            return identifier;
        }
    }

    private bool IsCurrentManagedIdentity(Contracts.RobloxProcessIdentity expected)
    {
        if (expected.Platform != Contracts.RobloxPlatform.MacOS || !expected.IsValid) return false;
        var current = _processLocator.FindProcess(expected.Pid);
        return current is not null && current.IsManaged
            && _processLocator.IsSameProcess(MacCoreProcessLocator.FromCore(expected), current);
    }

    private static T WithApplication<T>(int processId, Func<nint, T> action)
    {
        var application = NativeMethods.AXUIElementCreateApplication(processId);
        if (application == nint.Zero) return default!;
        try
        {
            return NativeMethods.AXUIElementGetPid(application, out var actualPid) == 0 && actualPid == processId
                ? action(application)
                : default!;
        }
        finally { Release(application); }
    }

    private static WindowCandidate? SelectWindow(IReadOnlyList<WindowCandidate> candidates)
    {
        if (candidates.Count == 0) return null;
        var main = candidates.Where(candidate => candidate.IsMain).ToArray();
        if (main.Length == 1) return main[0];
        if (candidates.Count == 1) return candidates[0];

        var ordered = candidates.OrderByDescending(candidate => candidate.Snapshot.Frame.Width * candidate.Snapshot.Frame.Height).ToArray();
        if (ordered.Length > 1)
        {
            var firstArea = ordered[0].Snapshot.Frame.Width * ordered[0].Snapshot.Frame.Height;
            var secondArea = ordered[1].Snapshot.Frame.Width * ordered[1].Snapshot.Frame.Height;
            if (Math.Abs(firstArea - secondArea) < 1) return null;
        }
        return ordered[0];
    }

    private static IReadOnlyList<WindowCandidate> EnumerateWindows(nint application)
    {
        var array = CopyAttribute(application, "AXWindows");
        if (array == nint.Zero) return Array.Empty<WindowCandidate>();
        try
        {
            var count = NativeMethods.CFArrayGetCount(array);
            var candidates = new List<WindowCandidate>();
            for (nint index = 0; index < count; index++)
            {
                var window = NativeMethods.CFArrayGetValueAtIndex(array, index);
                if (window == nint.Zero || NativeMethods.AXUIElementGetPid(window, out _) != 0) continue;
                var role = CopyStringAttribute(window, "AXRole");
                var subrole = CopyStringAttribute(window, "AXSubrole");
                if (!string.Equals(role, "AXWindow", StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(subrole)
                        && !string.Equals(subrole, "AXStandardWindow", StringComparison.Ordinal))) continue;
                if (!TryReadFrame(window, out var frame) || !frame.IsValid) continue;
                var title = CopyStringAttribute(window, "AXTitle");
                _ = NativeMethods.CFRetain(window);
                candidates.Add(new WindowCandidate(
                    window,
                    new MacAccessibleWindow(
                        string.Empty,
                        title,
                        frame,
                        CopyBooleanAttribute(window, "AXMinimized"),
                        CopyBooleanAttribute(window, "AXFullScreen")),
                    CopyBooleanAttribute(window, "AXMain")));
            }
            return candidates;
        }
        finally { Release(array); }
    }

    private static bool TryReadFrame(nint window, out MacWindowFrame frame)
    {
        frame = default;
        var positionValue = CopyAttribute(window, "AXPosition");
        var sizeValue = CopyAttribute(window, "AXSize");
        if (positionValue == nint.Zero || sizeValue == nint.Zero)
        {
            Release(positionValue);
            Release(sizeValue);
            return false;
        }
        try
        {
            if (!NativeMethods.AXValueGetValue(positionValue, AxValueCgPoint, out CGPoint position)
                || !NativeMethods.AXValueGetValue(sizeValue, AxValueCgSize, out CGSize size)) return false;
            frame = new MacWindowFrame(position.X, position.Y, size.Width, size.Height);
            return frame.IsValid;
        }
        finally
        {
            Release(positionValue);
            Release(sizeValue);
        }
    }

    private static nint CopyAttribute(nint element, string name)
    {
        var attribute = CreateString(name);
        if (attribute == nint.Zero) return nint.Zero;
        try
        {
            return NativeMethods.AXUIElementCopyAttributeValue(element, attribute, out var value) == 0
                ? value
                : nint.Zero;
        }
        finally { Release(attribute); }
    }

    private static string? CopyStringAttribute(nint element, string name)
    {
        var value = CopyAttribute(element, name);
        if (value == nint.Zero) return null;
        try { return ReadString(value); }
        finally { Release(value); }
    }

    private static bool CopyBooleanAttribute(nint element, string name)
    {
        var value = CopyAttribute(element, name);
        if (value == nint.Zero) return false;
        try { return NativeMethods.CFBooleanGetValue(value); }
        finally { Release(value); }
    }

    private static bool SetAttribute(nint element, string name, nint value)
    {
        var attribute = CreateString(name);
        if (attribute == nint.Zero || value == nint.Zero)
        {
            Release(attribute);
            return false;
        }
        try { return NativeMethods.AXUIElementSetAttributeValue(element, attribute, value) == 0; }
        finally { Release(attribute); }
    }

    private static nint CreateString(string value) =>
        NativeMethods.CFStringCreateWithCString(nint.Zero, value, Utf8Encoding);

    private static string? ReadString(nint value)
    {
        var length = NativeMethods.CFStringGetLength(value);
        var capacity = NativeMethods.CFStringGetMaximumSizeForEncoding(length, Utf8Encoding) + 1;
        if (capacity <= 1 || capacity > 65536) return null;
        var buffer = new byte[(int)capacity];
        return NativeMethods.CFStringGetCString(value, buffer, capacity, Utf8Encoding)
            ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length)
            : null;
    }

    private static nint GetBoolean(bool value)
    {
        nint library;
        lock (NativeLibraryGate)
        {
            _coreFoundationHandle = _coreFoundationHandle == nint.Zero
                ? NativeLibrary.Load(CoreFoundation)
                : _coreFoundationHandle;
            library = _coreFoundationHandle;
        }
        var symbol = NativeLibrary.GetExport(library, value ? "kCFBooleanTrue" : "kCFBooleanFalse");
        return Marshal.ReadIntPtr(symbol);
    }

    private static void ReleaseCandidates(IEnumerable<WindowCandidate> candidates)
    {
        foreach (var candidate in candidates) Release(candidate.Element);
    }

    private static void Release(nint value)
    {
        if (value != nint.Zero) NativeMethods.CFRelease(value);
    }

    private sealed record WindowCandidate(nint Element, MacAccessibleWindow Snapshot, bool IsMain);
    private sealed record RetainedWindow(Contracts.RobloxProcessIdentity Process, nint Element);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CGPoint(double X, double Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CGSize(double Width, double Height);

    private static class NativeMethods
    {
        [DllImport(ApplicationServices)] internal static extern nint AXUIElementCreateApplication(int pid);
        [DllImport(ApplicationServices)] internal static extern int AXUIElementGetPid(nint element, out int pid);
        [DllImport(ApplicationServices)] internal static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
        [DllImport(ApplicationServices)] internal static extern int AXUIElementSetAttributeValue(nint element, nint attribute, nint value);
        [DllImport(ApplicationServices)] internal static extern int AXUIElementPerformAction(nint element, nint action);
        [DllImport(ApplicationServices)] internal static extern nint AXValueCreate(int type, ref CGPoint value);
        [DllImport(ApplicationServices)] internal static extern nint AXValueCreate(int type, ref CGSize value);
        [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AXValueGetValue(nint value, int type, out CGPoint point);
        [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AXValueGetValue(nint value, int type, out CGSize size);
        [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool AXIsProcessTrusted();
        [DllImport(CoreFoundation)] internal static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(CoreFoundation)] internal static extern nint CFStringGetLength(nint value);
        [DllImport(CoreFoundation)] internal static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);
        [DllImport(CoreFoundation)] internal static extern nint CFArrayGetCount(nint array);
        [DllImport(CoreFoundation)] internal static extern nint CFArrayGetValueAtIndex(nint array, nint index);
        [DllImport(CoreFoundation)] internal static extern nint CFRetain(nint value);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFEqual(nint left, nint right);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFBooleanGetValue(nint value);
        [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
    }
}

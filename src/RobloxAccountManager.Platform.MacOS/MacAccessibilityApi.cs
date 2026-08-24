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

public sealed record MacAccessibilityWindowProbe(
    string DiagnosticCode,
    MacAccessibleWindow? Window,
    int TotalWindowCount,
    int EligibleWindowCount)
{
    public bool IsReady => Window is not null;

    public static MacAccessibilityWindowProbe Ready(
        MacAccessibleWindow window,
        int totalWindowCount,
        int eligibleWindowCount) =>
        new("accessible-window-ready", window, totalWindowCount, eligibleWindowCount);

    public static MacAccessibilityWindowProbe Failure(
        string diagnosticCode,
        int totalWindowCount = 0,
        int eligibleWindowCount = 0) =>
        new(diagnosticCode, null, totalWindowCount, eligibleWindowCount);
}

public readonly record struct MacAccessibilityOperationResult(bool Succeeded, string DiagnosticCode)
{
    public static MacAccessibilityOperationResult Success() => new(true, "accessibility-operation-ready");
    public static MacAccessibilityOperationResult Failure(string diagnosticCode) => new(false, diagnosticCode);
}

public interface IMacAccessibilityApi
{
    MacCapabilityResult GetCapability();
    MacAccessibilityWindowProbe ProbeMainWindow(Contracts.RobloxProcessIdentity process);
    MacAccessibilityOperationResult TrySetFrame(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        MacWindowFrame frame);
    MacAccessibilityOperationResult TrySetMinimized(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        bool minimized);
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

    public MacAccessibilityWindowProbe ProbeMainWindow(Contracts.RobloxProcessIdentity process)
    {
        var capability = GetCapability();
        if (!capability.IsSupported)
            return MacAccessibilityWindowProbe.Failure(capability.Code);
        if (!IsCurrentManagedIdentity(process))
            return MacAccessibilityWindowProbe.Failure("stale-process-identity");
        if (TryProbeRetainedWindow(process, out var retainedProbe))
            return retainedProbe;

        var probe = WithApplication(process.Pid, application =>
        {
            var enumeration = EnumerateWindows(application);
            try
            {
                var selected = SelectWindow(enumeration.Candidates);
                if (selected is null)
                {
                    var code = enumeration.HasTransientCandidate
                        ? "accessibility-window-not-settled"
                        : enumeration.TotalWindowCount == 0
                        ? "accessibility-no-windows"
                        : enumeration.Candidates.Count == 0
                            ? "accessibility-no-eligible-window"
                            : "accessibility-window-ambiguous";
                    return MacAccessibilityWindowProbe.Failure(
                        code,
                        enumeration.TotalWindowCount,
                        enumeration.Candidates.Count);
                }
                if (!IsCurrentManagedIdentity(process))
                    return MacAccessibilityWindowProbe.Failure(
                        "stale-process-identity",
                        enumeration.TotalWindowCount,
                        enumeration.Candidates.Count);

                var window = selected.Snapshot with
                {
                    Identifier = RetainWindow(process, selected.Element, selected.Snapshot)
                };
                return MacAccessibilityWindowProbe.Ready(
                    window,
                    enumeration.TotalWindowCount,
                    enumeration.Candidates.Count);
            }
            finally { ReleaseCandidates(enumeration.Candidates); }
        });
        return probe ?? MacAccessibilityWindowProbe.Failure("accessibility-application-unavailable");
    }

    public MacAccessibilityOperationResult TrySetFrame(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        MacWindowFrame frame)
    {
        if (!frame.IsValid || string.IsNullOrWhiteSpace(windowIdentifier))
            return MacAccessibilityOperationResult.Failure("accessibility-frame-request-invalid");
        var result = WithSelectedWindow(process, windowIdentifier, window =>
        {
            var position = new CGPoint(frame.Left, frame.Top);
            var size = new CGSize(frame.Width, frame.Height);
            var positionValue = NativeMethods.AXValueCreate(AxValueCgPoint, ref position);
            var sizeValue = NativeMethods.AXValueCreate(AxValueCgSize, ref size);
            if (positionValue == nint.Zero || sizeValue == nint.Zero)
            {
                Release(positionValue);
                Release(sizeValue);
                return MacAccessibilityOperationResult.Failure("accessibility-frame-value-create-failed");
            }

            try
            {
                // Resize before moving so an application-enforced size adjustment cannot
                // recenter the final top-left position after it has already been written.
                var sizeError = SetAttributeWithRetry(window, "AXSize", sizeValue);
                if (sizeError != 0)
                    return MacAccessibilityOperationResult.Failure(
                        $"accessibility-frame-size-{DescribeAxError(sizeError)}");
                var positionError = SetAttributeWithRetry(window, "AXPosition", positionValue);
                return positionError == 0
                    ? MacAccessibilityOperationResult.Success()
                    : MacAccessibilityOperationResult.Failure(
                        $"accessibility-frame-position-{DescribeAxError(positionError)}");
            }
            finally
            {
                Release(positionValue);
                Release(sizeValue);
            }
        });
        return string.IsNullOrWhiteSpace(result.DiagnosticCode)
            ? MacAccessibilityOperationResult.Failure("accessibility-window-reference-unavailable")
            : result;
    }

    public MacAccessibilityOperationResult TrySetMinimized(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        bool minimized)
    {
        if (string.IsNullOrWhiteSpace(windowIdentifier))
            return MacAccessibilityOperationResult.Failure("accessibility-minimized-request-invalid");
        var result = WithSelectedWindow(process, windowIdentifier,
            window =>
            {
                var error = SetAttributeWithRetry(window, "AXMinimized", GetBoolean(minimized));
                return error == 0
                    ? MacAccessibilityOperationResult.Success()
                    : MacAccessibilityOperationResult.Failure(
                        $"accessibility-minimized-{DescribeAxError(error)}");
            });
        return string.IsNullOrWhiteSpace(result.DiagnosticCode)
            ? MacAccessibilityOperationResult.Failure("accessibility-window-reference-unavailable")
            : result;
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

    private T WithSelectedWindow<T>(
        Contracts.RobloxProcessIdentity process,
        string identifier,
        Func<nint, T> action)
    {
        if (!OperatingSystem.IsMacOS() || !IsCurrentManagedIdentity(process)) return default!;
        RetainedWindow retained;
        nint retainedElement;
        lock (_windowsGate)
        {
            if (!_retainedWindows.TryGetValue(identifier, out retained!)
                || !retained.Process.Matches(process)) return default!;
            retainedElement = NativeMethods.CFRetain(retained.Element);
        }
        try
        {
            return NativeMethods.AXUIElementGetPid(retainedElement, out var actualPid) == 0
                && actualPid == process.Pid
                && IsCurrentManagedIdentity(process)
                    ? action(retainedElement)
                    : default!;
        }
        finally { Release(retainedElement); }
    }

    private bool TryProbeRetainedWindow(
        Contracts.RobloxProcessIdentity process,
        out MacAccessibilityWindowProbe probe)
    {
        string? identifier = null;
        nint element = nint.Zero;
        MacAccessibleWindow? fallback = null;
        lock (_windowsGate)
        {
            var existing = _retainedWindows.FirstOrDefault(pair =>
                pair.Value.Process.Matches(process));
            if (!string.IsNullOrWhiteSpace(existing.Key))
            {
                identifier = existing.Key;
                element = NativeMethods.CFRetain(existing.Value.Element);
                fallback = existing.Value.Snapshot;
            }
        }

        if (identifier is null || element == nint.Zero)
        {
            probe = null!;
            return false;
        }

        try
        {
            if (TryReadWindowCandidate(element, process.Pid, fallback, out var candidate, out var transient))
            {
                lock (_windowsGate)
                {
                    if (_retainedWindows.TryGetValue(identifier, out var current)
                        && current.Process.Matches(process)
                        && current.Element == element)
                    {
                        _retainedWindows[identifier] = current with { Snapshot = candidate.Snapshot };
                    }
                }
                probe = MacAccessibilityWindowProbe.Ready(
                    candidate.Snapshot with { Identifier = identifier },
                    totalWindowCount: 1,
                    eligibleWindowCount: 1);
                return true;
            }
            if (transient)
            {
                probe = MacAccessibilityWindowProbe.Failure(
                    "accessibility-window-not-settled",
                    totalWindowCount: 1);
                return true;
            }
        }
        finally { Release(element); }

        probe = null!;
        return false;
    }

    private string RetainWindow(
        Contracts.RobloxProcessIdentity process,
        nint element,
        MacAccessibleWindow snapshot)
    {
        lock (_windowsGate)
        {
            foreach (var stale in _retainedWindows
                         .Where(pair => pair.Value.Process.Matches(process))
                         .ToArray())
            {
                _retainedWindows.Remove(stale.Key);
                Release(stale.Value.Element);
            }

            var identifier = $"ax-window-{Guid.NewGuid():N}";
            _ = NativeMethods.CFRetain(element);
            _retainedWindows.Add(identifier, new RetainedWindow(process, element, snapshot));
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

    private static WindowEnumeration EnumerateWindows(nint application)
    {
        var array = CopyAttribute(application, "AXWindows");
        if (array == nint.Zero) return new WindowEnumeration(0, [], false);
        try
        {
            var count = NativeMethods.CFArrayGetCount(array);
            var candidates = new List<WindowCandidate>();
            var hasTransientCandidate = false;
            for (nint index = 0; index < count; index++)
            {
                var window = NativeMethods.CFArrayGetValueAtIndex(array, index);
                if (!TryReadWindowCandidate(
                        window,
                        expectedPid: null,
                        fallback: null,
                        out var candidate,
                        out var transient))
                {
                    hasTransientCandidate |= transient;
                    continue;
                }
                _ = NativeMethods.CFRetain(window);
                candidates.Add(candidate);
            }
            return new WindowEnumeration((int)count, candidates, hasTransientCandidate);
        }
        finally { Release(array); }
    }

    private static bool TryReadWindowCandidate(
        nint window,
        int? expectedPid,
        MacAccessibleWindow? fallback,
        out WindowCandidate candidate,
        out bool transient)
    {
        candidate = null!;
        transient = false;
        if (window == nint.Zero
            || NativeMethods.AXUIElementGetPid(window, out var actualPid) != 0
            || expectedPid is int pid && actualPid != pid) return false;
        var role = CopyStringAttribute(window, "AXRole");
        var subrole = CopyStringAttribute(window, "AXSubrole");
        if (!string.Equals(role, "AXWindow", StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(subrole)
                && !string.Equals(subrole, "AXStandardWindow", StringComparison.Ordinal))) return false;
        var hasMinimizedState = TryCopyBooleanAttribute(window, "AXMinimized", out var minimized);
        if (!hasMinimizedState)
        {
            transient = true;
            return false;
        }
        if (!TryReadFrame(window, out var frame) || !frame.IsValid)
        {
            // AX attributes can be temporarily unavailable while Roblox is
            // replacing or settling a native window. Do not publish a stale
            // retained frame as a fresh readback; the manager will retry.
            transient = true;
            return false;
        }
        candidate = new WindowCandidate(
            window,
            new MacAccessibleWindow(
                string.Empty,
                CopyStringAttribute(window, "AXTitle") ?? fallback?.Title,
                frame,
                minimized,
                CopyBooleanAttribute(window, "AXFullScreen")),
            CopyBooleanAttribute(window, "AXMain"));
        return true;
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

    private static bool TryCopyBooleanAttribute(nint element, string name, out bool value)
    {
        value = false;
        var nativeValue = CopyAttribute(element, name);
        if (nativeValue == nint.Zero) return false;
        try
        {
            value = NativeMethods.CFBooleanGetValue(nativeValue);
            return true;
        }
        finally { Release(nativeValue); }
    }

    private static bool CopyBooleanAttribute(nint element, string name) =>
        TryCopyBooleanAttribute(element, name, out var value) && value;

    private static int SetAttribute(nint element, string name, nint value)
    {
        var attribute = CreateString(name);
        if (attribute == nint.Zero || value == nint.Zero)
        {
            Release(attribute);
            return -25201;
        }
        try { return NativeMethods.AXUIElementSetAttributeValue(element, attribute, value); }
        finally { Release(attribute); }
    }

    private static int SetAttributeWithRetry(nint element, string name, nint value)
    {
        const int cannotComplete = -25204;
        var error = SetAttribute(element, name, value);
        for (var attempt = 1; attempt < 3 && error == cannotComplete; attempt++)
        {
            Thread.Sleep(25);
            error = SetAttribute(element, name, value);
        }
        return error;
    }

    private static string DescribeAxError(int error) => error switch
    {
        -25200 => "failure",
        -25201 => "illegal-argument",
        -25202 => "invalid-ui-element",
        -25204 => "cannot-complete",
        -25205 => "attribute-unsupported",
        -25208 => "not-implemented",
        -25211 => "api-disabled",
        -25212 => "no-value",
        -25214 => "not-enough-precision",
        _ => $"ax-error-{Math.Abs(error)}"
    };

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
    private sealed record WindowEnumeration(
        int TotalWindowCount,
        IReadOnlyList<WindowCandidate> Candidates,
        bool HasTransientCandidate);
    private sealed record RetainedWindow(
        Contracts.RobloxProcessIdentity Process,
        nint Element,
        MacAccessibleWindow Snapshot);
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
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFBooleanGetValue(nint value);
        [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
    }
}

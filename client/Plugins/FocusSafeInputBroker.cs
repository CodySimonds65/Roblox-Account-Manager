using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

public sealed record BackgroundInputResult(
    bool Accepted,
    string Code,
    string Message,
    int PostedCount,
    nint ForegroundBefore,
    nint ForegroundAfter)
{
    /// <summary>Concrete transport used for this result.</summary>
    public string DeliveryMode { get; init; } = "unknown";

    /// <summary>Posted messages are intentionally reported as unverified.</summary>
    public string Verification { get; init; } = "unverified";

    /// <summary>Optional host trace/correlation id for diagnostic probes.</summary>
    public string? TraceId { get; init; }

    public int RequestedCount { get; init; }
    public nint TargetRootWindow { get; init; }
    public nint TargetRenderWindow { get; init; }
    public int TargetProcessId { get; init; }
    public long TargetProcessStartTimeUtcTicks { get; init; }
    /// <summary>Read-only cursor observation captured with the delivery result.</summary>
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    public string? SelectedAccountId { get; init; }
    public bool? SelectedVisible { get; init; }

    public static BackgroundInputResult Failure(string code, string message, nint before, nint after, int posted = 0) =>
        new(false, code, message, posted, before, after)
        {
            DeliveryMode = "none",
            Verification = "not-delivered"
        };
}

public sealed class FocusSafeInputBroker
{
    public BackgroundInputResult Post(
        ManagedAccountSnapshot account,
        IReadOnlyList<PluginInputEvent> events,
        Func<nint, bool>? windowValidator = null,
        InputDeliveryIntent deliveryIntent = InputDeliveryIntent.Default,
        string? traceId = null,
        Func<ManagedAccountSnapshot?>? accountResolver = null)
    {
        return PostAsync(account, events, CancellationToken.None, windowValidator, deliveryIntent, traceId, accountResolver).GetAwaiter().GetResult();
    }

    public async Task<BackgroundInputResult> PostAsync(
        ManagedAccountSnapshot account,
        IReadOnlyList<PluginInputEvent> events,
        CancellationToken cancellationToken,
        Func<nint, bool>? windowValidator = null,
        InputDeliveryIntent deliveryIntent = InputDeliveryIntent.Default,
        string? traceId = null,
        Func<ManagedAccountSnapshot?>? accountResolver = null)
    {
        if (events.Count == 0)
        {
            return Stamp(BackgroundInputResult.Failure("unavailable", "No input events were supplied.", GetForegroundWindow(), GetForegroundWindow()), account, 0, traceId, deliveryIntent);
        }
        if (accountResolver is not null)
        {
            var resolved = accountResolver.Invoke();
            if (resolved is null)
            {
                return Stamp(BackgroundInputResult.Failure("stale-window", "The managed account is stopping or its process identity is stale.", GetForegroundWindow(), GetForegroundWindow()), account, events.Count, traceId, deliveryIntent);
            }
            account = resolved;
        }
        if (account.WindowHandle == nint.Zero || account.IsMinimized)
        {
            return Stamp(BackgroundInputResult.Failure("unavailable", "The target window is unavailable or minimized.", GetForegroundWindow(), GetForegroundWindow()), account, events.Count, traceId, deliveryIntent);
        }

        var hostIntegrity = ProcessIntegrity.Current;
        var targetIntegrity = ProcessIntegrity.ForWindow(account.WindowHandle);
        if (hostIntegrity == ProcessIntegrityLevel.Unknown || targetIntegrity == ProcessIntegrityLevel.Unknown)
        {
            return Stamp(BackgroundInputResult.Failure("integrity-unknown",
                "The integrity level of RAM or the target client could not be verified; background delivery was blocked.",
                GetForegroundWindow(), GetForegroundWindow()), account, events.Count, traceId, deliveryIntent);
        }
        if (targetIntegrity > hostIntegrity)
        {
            return Stamp(BackgroundInputResult.Failure("integrity-mismatch",
                $"The target client is {targetIntegrity} integrity while RAM is {hostIntegrity}; background delivery was blocked.",
                GetForegroundWindow(), GetForegroundWindow()), account, events.Count, traceId, deliveryIntent);
        }

        if (!ValidateWindowIdentity(account, account.WindowHandle, requireMetrics: true) ||
            (windowValidator is not null && !windowValidator(account.WindowHandle)))
        {
            return Stamp(BackgroundInputResult.Failure("stale-window", "The target window is no longer valid.", GetForegroundWindow(), GetForegroundWindow()), account, events.Count, traceId, deliveryIntent);
        }

        var foregroundBefore = GetForegroundWindow();
        var posted = 0;
        long previousOffset = 0;
        var postedEvents = new List<PluginInputEvent>();
        try
        {
            foreach (var input in events.OrderBy(item => item.OffsetMicroseconds))
            {
                // Macro timing must be honored: pace from time zero to the first event's
                // offset, then from event to event. 1 microsecond equals 10 ticks.
                var gapMicroseconds = input.OffsetMicroseconds - previousOffset;
                if (gapMicroseconds > 0)
                {
                    await Task.Delay(TimeSpan.FromTicks(gapMicroseconds * 10), cancellationToken).ConfigureAwait(false);
                }
                previousOffset = input.OffsetMicroseconds;

                cancellationToken.ThrowIfCancellationRequested();

                // Re-resolve immediately before every post: HWND values are reusable and
                // Roblox can recreate its render child while a macro is running.
                var liveAccount = accountResolver is null ? account : accountResolver.Invoke();
                if (liveAccount is null ||
                    !ValidateWindowIdentity(liveAccount, liveAccount.WindowHandle, requireMetrics: true) ||
                    (windowValidator is not null && !windowValidator(liveAccount.WindowHandle)))
                {
                    await ReleaseHeldInputsAsync(liveAccount ?? account, postedEvents, windowValidator, accountResolver).ConfigureAwait(false);
                    var after = GetForegroundWindow();
                    return Stamp(BackgroundInputResult.Failure("stale-window", "The target HWND changed or its client metrics no longer match.", foregroundBefore, after, posted), account, events.Count, traceId, deliveryIntent);
                }
                var safeAccount = liveAccount;
                var destination = IsKeyboard(input) && safeAccount.RootWindowHandle != nint.Zero
                    ? safeAccount.RootWindowHandle : safeAccount.WindowHandle;
                var destinationValid = ValidateWindowIdentity(safeAccount, destination, requireMetrics: destination != safeAccount.RootWindowHandle) &&
                    (windowValidator is null || windowValidator(destination));
                if (!destinationValid)
                {
                    await ReleaseHeldInputsAsync(safeAccount, postedEvents, windowValidator, accountResolver).ConfigureAwait(false);
                    var foregroundAfterFailure = GetForegroundWindow();
                    return Stamp(BackgroundInputResult.Failure("stale-window", "The final input destination is no longer valid.", foregroundBefore, foregroundAfterFailure, posted), account, events.Count, traceId, deliveryIntent);
                }
                if (!TryPost(destination, safeAccount, input, out var error))
                {
                    await ReleaseHeldInputsAsync(safeAccount, postedEvents, windowValidator, accountResolver).ConfigureAwait(false);
                    var foregroundAfterFailure = GetForegroundWindow();
                    return Stamp(BackgroundInputResult.Failure(error.Code, error.Message, foregroundBefore, foregroundAfterFailure, posted), account, events.Count, traceId, deliveryIntent);
                }
                postedEvents.Add(input);
                posted++;
            }
        }
        catch (OperationCanceledException)
        {
            await ReleaseHeldInputsAsync(account, postedEvents, windowValidator, accountResolver).ConfigureAwait(false);
            var foregroundAfterCancel = GetForegroundWindow();
            return Stamp(BackgroundInputResult.Failure("canceled", "Input dispatch was canceled; held input cleanup was attempted.", foregroundBefore, foregroundAfterCancel, posted), account, events.Count, traceId, deliveryIntent);
        }

        // Never leave a logical key/button down after a detached best-effort run.
        await ReleaseHeldInputsAsync(account, postedEvents, windowValidator, accountResolver).ConfigureAwait(false);
        var foregroundAfter = GetForegroundWindow();
        // The broker never restores focus. A changed foreground window is reported to
        // the caller so an external user action cannot be mistaken for our own focus work.
        return Stamp(new BackgroundInputResult(true, foregroundBefore == foregroundAfter ? "ok" : "foreground-changed",
            foregroundBefore == foregroundAfter ? "Messages queued; consumption is unverified." : "Messages queued; foreground changed externally and consumption is unverified.",
            posted, foregroundBefore, foregroundAfter)
        {
            DeliveryMode = "post-message",
            Verification = "unverified"
        }, account, events.Count, traceId, deliveryIntent);
    }

    private static async Task ReleaseHeldInputsAsync(ManagedAccountSnapshot account,
        IReadOnlyList<PluginInputEvent> postedEvents, Func<nint, bool>? windowValidator,
        Func<ManagedAccountSnapshot?>? accountResolver)
    {
        var releases = InputSendInjector.PendingReleases(postedEvents);
        var live = accountResolver is null ? account : accountResolver.Invoke();
        if (live is null) return;
        if (releases.Count == 0 || !ValidateWindowIdentity(live, live.WindowHandle, requireMetrics: true) ||
            (windowValidator is not null && !windowValidator(live.WindowHandle))) return;
        foreach (var release in releases)
        {
            var destination = IsKeyboard(release) && live.RootWindowHandle != nint.Zero ? live.RootWindowHandle : live.WindowHandle;
            if (!ValidateWindowIdentity(live, destination, requireMetrics: destination != live.RootWindowHandle) ||
                (windowValidator is not null && !windowValidator(destination)) ||
                !TryPost(destination, live, release, out _)) break;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool IsKeyboard(PluginInputEvent input) =>
        input.Kind is PluginInputKind.KeyDown or PluginInputKind.KeyUp;

    private static BackgroundInputResult Stamp(BackgroundInputResult result, ManagedAccountSnapshot account,
        int requestedCount, string? traceId, InputDeliveryIntent deliveryIntent) => result with
        {
            DeliveryMode = result.DeliveryMode == "post-message"
            ? deliveryIntent == InputDeliveryIntent.PostMessageProbe ? "post-message-probe" : "post-message"
            : result.DeliveryMode,
            TraceId = traceId,
            RequestedCount = requestedCount,
            TargetRootWindow = account.RootWindowHandle,
            TargetRenderWindow = account.WindowHandle,
            TargetProcessId = account.ProcessId,
            TargetProcessStartTimeUtcTicks = account.ProcessStartTimeUtcTicks,
            CursorX = TryGetCursor(out var point) ? point.X : 0,
            CursorY = point.Y
        };

    private static bool TryGetCursor(out POINT point)
    {
        point = default;
        try { return GetCursorPos(out point); }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool ValidateWindowIdentity(ManagedAccountSnapshot account, nint hwnd, bool requireMetrics)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd) || IsIconic(GetAncestor(hwnd, GA_ROOT))) return false;
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == nint.Zero || !IsWindow(root)) return false;
        if (account.RootWindowHandle != nint.Zero && root != account.RootWindowHandle) return false;
        GetWindowThreadProcessId(hwnd, out var ownerPid);
        if (ownerPid != account.ProcessId) return false;
        GetWindowThreadProcessId(root, out var rootOwnerPid);
        if (rootOwnerPid != account.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(account.ProcessId);
            process.Refresh();
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != account.ProcessStartTimeUtcTicks) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }

        if (!requireMetrics) return true;
        if (!GetClientRect(hwnd, out var client)) return false;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return false;
        var width = Math.Max(0, client.Right - client.Left);
        var height = Math.Max(0, client.Bottom - client.Top);
        return origin.X == account.ClientX && origin.Y == account.ClientY &&
               width == account.ClientWidth && height == account.ClientHeight;
    }

    private static bool TryPost(nint hwnd, ManagedAccountSnapshot account, PluginInputEvent input, out (string Code, string Message) error)
    {
        error = default;
        // Key events are delivered to the window's top-level HWND: Chromium-style
        // clients (Roblox) process keyboard at the browser window, not the render child.
        if (input.Kind is PluginInputKind.KeyDown or PluginInputKind.KeyUp && account.RootWindowHandle != nint.Zero)
            hwnd = account.RootWindowHandle;
        var message = 0u;
        nuint wParam = 0;
        nint lParam = 0;
        switch (input.Kind)
        {
            case PluginInputKind.KeyDown:
                message = input.Extended ? WM_KEYDOWN : WM_KEYDOWN;
                wParam = (nuint)Math.Clamp(input.VirtualKey, 0, ushort.MaxValue);
                lParam = BuildKeyLParam(input.ScanCode, input.Extended, keyUp: false);
                break;
            case PluginInputKind.KeyUp:
                message = WM_KEYUP;
                wParam = (nuint)Math.Clamp(input.VirtualKey, 0, ushort.MaxValue);
                lParam = BuildKeyLParam(input.ScanCode, input.Extended, keyUp: true);
                break;
            case PluginInputKind.MouseMove:
                message = WM_MOUSEMOVE;
                wParam = 0;
                lParam = PackClientPoint(account, input.NormalizedX, input.NormalizedY);
                break;
            case PluginInputKind.MouseButtonDown:
                message = input.Button switch { 0 => WM_LBUTTONDOWN, 1 => WM_RBUTTONDOWN, 2 => WM_MBUTTONDOWN, _ => 0 };
                wParam = input.Button switch { 0 => MK_LBUTTON, 1 => MK_RBUTTON, 2 => MK_MBUTTON, _ => 0 };
                lParam = PackClientPoint(account, input.NormalizedX, input.NormalizedY);
                break;
            case PluginInputKind.MouseButtonUp:
                message = input.Button switch { 0 => WM_LBUTTONUP, 1 => WM_RBUTTONUP, 2 => WM_MBUTTONUP, _ => 0 };
                wParam = 0;
                lParam = PackClientPoint(account, input.NormalizedX, input.NormalizedY);
                break;
            case PluginInputKind.MouseWheel:
                message = WM_MOUSEWHEEL;
                wParam = (nuint)((input.WheelDelta & 0xffff) << 16);
                lParam = PackScreenPoint(account, input.NormalizedX, input.NormalizedY);
                break;
            default:
                error = ("unsupported-input", "The input event kind is not supported.");
                return false;
        }

        if (message == 0)
        {
            error = ("unsupported-input", "The input event contains an unsupported button or message.");
            return false;
        }

        SetLastError(0);
        if (!PostMessage(hwnd, message, wParam, lParam))
        {
            var win32 = Marshal.GetLastWin32Error();
            error = (win32 == 5 ? "access-denied" : "post-failed", new Win32Exception(win32).Message);
            return false;
        }

        return true;
    }

    private static nint BuildKeyLParam(int scanCode, bool extended, bool keyUp)
    {
        var value = 1u | ((uint)Math.Clamp(scanCode, 0, 255) << 16);
        if (extended) value |= 1u << 24;
        if (keyUp) value |= (1u << 30) | (1u << 31);
        return unchecked((nint)value);
    }

    private static nint PackClientPoint(ManagedAccountSnapshot account, double normalizedX, double normalizedY)
    {
        var x = Math.Clamp((int)Math.Round(normalizedX * Math.Max(0, account.ClientWidth - 1)), 0, short.MaxValue);
        var y = Math.Clamp((int)Math.Round(normalizedY * Math.Max(0, account.ClientHeight - 1)), 0, short.MaxValue);
        return (nint)((y << 16) | (x & 0xffff));
    }

    private static nint PackScreenPoint(ManagedAccountSnapshot account, double normalizedX, double normalizedY)
    {
        var x = account.ClientX + Math.Clamp((int)Math.Round(normalizedX * Math.Max(0, account.ClientWidth - 1)), 0, short.MaxValue);
        var y = account.ClientY + Math.Clamp((int)Math.Round(normalizedY * Math.Max(0, account.ClientHeight - 1)), 0, short.MaxValue);
        return (nint)((y << 16) | (x & 0xffff));
    }

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const nuint MK_LBUTTON = 0x0001;
    private const nuint MK_RBUTTON = 0x0002;
    private const nuint MK_MBUTTON = 0x0010;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT point);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint errorCode);

    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    private struct POINT { public int X; public int Y; }
    private const uint GA_ROOT = 2;
}

public sealed class AccountInputLeaseCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _leases = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string accountId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var gate = _leases.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)) return null;
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class PriorityInputLeaseCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string accountId, string owner, int priority,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(owner, priority);
        AccountState state;
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountId, out state!)) _accounts[accountId] = state = new AccountState();
            state.Waiters.Add(waiter);
            GrantNext(state);
        }
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await waiter.Granted.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new Lease(this, accountId, state, waiter);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(state.Current, waiter))
                {
                    state.Current = null;
                    state.Active = false;
                }
                else state.Waiters.Remove(waiter);
                GrantNext(state);
                if (!state.Active && state.Waiters.Count == 0) _accounts.Remove(accountId);
            }
            return null;
        }
    }

    private void Release(string accountId, AccountState state, Waiter waiter)
    {
        lock (_gate)
        {
            if (!state.Active || !ReferenceEquals(state.Current, waiter)) return;
            state.Active = false; state.Current = null; GrantNext(state);
            if (!state.Active && state.Waiters.Count == 0) _accounts.Remove(accountId);
        }
    }

    private static void GrantNext(AccountState state)
    {
        if (state.Active || state.Waiters.Count == 0) return;
        var next = state.Waiters.OrderByDescending(waiter => waiter.Priority).ThenBy(waiter => waiter.Sequence).First();
        state.Waiters.Remove(next); state.Current = next; state.Active = true; next.Granted.TrySetResult(true);
    }

    private sealed class AccountState { public bool Active; public Waiter? Current; public List<Waiter> Waiters { get; } = []; }
    private sealed class Waiter(string owner, int priority)
    {
        private static long _nextSequence;
        public string Owner { get; } = owner; public int Priority { get; } = priority;
        public long Sequence { get; } = Interlocked.Increment(ref _nextSequence);
        public TaskCompletionSource<bool> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Lease(PriorityInputLeaseCoordinator owner, string accountId, AccountState state, Waiter waiter) : IAsyncDisposable
    { public ValueTask DisposeAsync() { owner.Release(accountId, state, waiter); return ValueTask.CompletedTask; } }
}

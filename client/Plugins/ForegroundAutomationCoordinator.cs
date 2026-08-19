using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Owns the single desktop-wide foreground automation lane. Hardware-style
/// targetable per HWND, so every plugin action must acquire this coordinator,
/// foreground one validated Roblox root, and release the lane before another
/// plugin can automate a different account.
/// </summary>
public sealed class ForegroundAutomationCoordinator : IAsyncDisposable
{
    private readonly RunningAccountRegistry _accounts;
    private readonly ClientEmbeddingService _embeddings;
    private readonly InputSendInjector _injector;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public ForegroundAutomationCoordinator(
        RunningAccountRegistry accounts,
        ClientEmbeddingService embeddings,
        InputSendInjector injector)
    {
        _accounts = accounts;
        _embeddings = embeddings;
        _injector = injector;
    }

    public async Task<AutomationSessionResult> OpenAsync(
        string pluginId,
        IReadOnlyList<string> accountIds,
        CancellationToken cancellationToken,
        bool restoreForeground = true)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || accountIds is null || accountIds.Count == 0)
            return AutomationSessionResult.Fail("invalid-request", "A foreground session requires a plugin and at least one account.");

        var distinct = accountIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != accountIds.Count)
            return AutomationSessionResult.Fail("invalid-request", "Foreground session account IDs must be unique and non-empty.");
        if (distinct.Length > 64)
            return AutomationSessionResult.Fail("quota", "A foreground session may target at most 64 accounts.");

        try
        {
            if (!await _lane.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
                return AutomationSessionResult.Fail("busy", "Another foreground automation session is active.");
        }
        catch (OperationCanceledException)
        {
            return AutomationSessionResult.Fail("cancelled", "The foreground session was canceled before it started.");
        }

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    _lane.Release();
                    return AutomationSessionResult.Fail("unavailable", "Foreground automation is shutting down.");
                }

                var snapshot = ForegroundSnapshot.Capture(_embeddings.VisibleAccountId);
                var session = new Session(Guid.NewGuid().ToString("N"), pluginId, distinct, snapshot, restoreForeground);
                _sessions.Add(session.Id, session);
                return AutomationSessionResult.Ok(session.Id, snapshot);
            }
        }
        catch
        {
            _lane.Release();
            throw;
        }
    }

    public async Task<AutomationSessionResult> ActivateAsync(
        string pluginId,
        string sessionId,
        string accountId,
        CancellationToken cancellationToken)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out session) || !string.Equals(session.PluginId, pluginId, StringComparison.Ordinal))
                return AutomationSessionResult.Fail("session-not-found", "The foreground automation session is not owned by this plugin.");
            if (!session.AccountIds.Contains(accountId, StringComparer.Ordinal))
                return AutomationSessionResult.Fail("invalid-request", "The account is not part of this foreground session.");
            if (session.UserTookOver)
                return AutomationSessionResult.Fail("user-takeover", "Automation stopped after the user took foreground focus.");

            // Once a session has activated an account, a foreground change is
            // an explicit user takeover. Do not fight Alt-Tab/clicks by
            // activating the next account after the user has taken control.
            if (session.ActiveAccountId is not null && session.LastRoot != nint.Zero &&
                GetForegroundWindow() != session.LastRoot)
            {
                session.UserTookOver = true;
                return AutomationSessionResult.Fail("user-takeover", "Automation stopped after the user took foreground focus.");
            }
        }

        if (!_accounts.TryResolveLiveAccount(accountId, out var account))
            return AutomationSessionResult.Fail("stale-window", "The managed account process or window is no longer valid.");

        var trackedRoot = _embeddings.TrackedRootFor(accountId);
        var root = ResolveRoot(accountId, account);
        if (trackedRoot is not null && _embeddings.RootFor(accountId) is null)
            return AutomationSessionResult.Fail("stale-window", "The managed client docking identity drifted before activation.");
        if (!IsValidatedRoot(root, account) ||
            (account.RootWindowHandle != nint.Zero && root != account.RootWindowHandle))
            return AutomationSessionResult.Fail("stale-window", "The managed Roblox root window is unavailable.");

        var embedded = _embeddings.RootFor(accountId);
        if (embedded is not null && embedded.Value == root)
        {
            _embeddings.ShowOnly(accountId);
            _embeddings.Layout();
        }

        // Layout/show operations can race a Roblox restart. Revalidate the
        // process identity immediately before the only activation call.
        if (!_accounts.TryResolveLiveAccount(accountId, out var preActivation) ||
            preActivation.ProcessId != account.ProcessId ||
            preActivation.ProcessStartTimeUtcTicks != account.ProcessStartTimeUtcTicks ||
            !IsValidatedRoot(root, preActivation))
            return AutomationSessionResult.Fail("stale-window", "The managed Roblox identity changed before activation.");

        if (!IsWindowVisible(root)) ShowWindow(root, SwShow);
        if (IsIconic(root)) ShowWindow(root, SwRestore);
        if (!_accounts.TryResolveLiveAccount(accountId, out var beforeActivation) ||
            beforeActivation.ProcessId != account.ProcessId ||
            beforeActivation.ProcessStartTimeUtcTicks != account.ProcessStartTimeUtcTicks ||
            !IsValidatedRoot(root, beforeActivation))
            return AutomationSessionResult.Fail("stale-window", "The managed Roblox identity changed before activation.");

        // This is the only approved activation call in the product. It is
        // deliberately guarded by live identity and followed by exact-root
        // foreground validation before any injected event is made.
        if (!SetForegroundWindow(root))
            return AutomationSessionResult.Fail("focus-denied", "Windows denied foreground activation for the managed Roblox client.");

        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.5);
        while (GetForegroundWindow() != root)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadline)
                return AutomationSessionResult.Fail("focus-denied", "The managed Roblox client did not become foreground in time.");
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        if (!_accounts.TryResolveLiveAccount(accountId, out var current) ||
            current.ProcessId != account.ProcessId ||
            current.ProcessStartTimeUtcTicks != account.ProcessStartTimeUtcTicks ||
            ResolveRoot(accountId, current) != root)
            return AutomationSessionResult.Fail("stale-window", "The managed Roblox identity changed during activation.");

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var currentSession) || !ReferenceEquals(currentSession, session))
                return AutomationSessionResult.Fail("session-not-found", "The foreground automation session ended during activation.");
            session.ActiveAccountId = accountId;
            session.LastRoot = root;
        }
        return AutomationSessionResult.Ok(sessionId, session.Snapshot) with { AccountId = accountId, RootWindow = root };
    }

    public async Task<BackgroundInputResult> DispatchAsync(
        string pluginId,
        string sessionId,
        string accountId,
        IReadOnlyList<PluginInputEvent> events,
        string? traceId,
        CancellationToken cancellationToken)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out session) || !string.Equals(session.PluginId, pluginId, StringComparison.Ordinal))
                return BackgroundInputResult.Failure("session-not-found", "The foreground automation session is not owned by this plugin.", nint.Zero, nint.Zero);
            if (!string.Equals(session.ActiveAccountId, accountId, StringComparison.Ordinal) || session.LastRoot == nint.Zero)
                return BackgroundInputResult.Failure("account-not-active", "Activate the account before dispatching foreground input.", nint.Zero, nint.Zero);
            if (session.UserTookOver)
                return BackgroundInputResult.Failure("user-takeover", "Automation stopped after the user took foreground focus.", GetForegroundWindow(), GetForegroundWindow());
        }

        if (!_accounts.TryResolveLiveAccount(accountId, out var expected))
            return BackgroundInputResult.Failure("stale-window", "The managed account process or window is no longer valid.", nint.Zero, nint.Zero);
        var trackedRoot = _embeddings.TrackedRootFor(accountId);
        var root = ResolveRoot(accountId, expected);
        if (trackedRoot is not null && _embeddings.RootFor(accountId) is null)
            return BackgroundInputResult.Failure("stale-window", "The managed client docking identity drifted during automation.", nint.Zero, nint.Zero);
        if (root == nint.Zero || root != session.LastRoot)
            return BackgroundInputResult.Failure("stale-window", "The managed Roblox root changed during automation.", nint.Zero, nint.Zero);

        bool TargetStillValid()
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, session) ||
                    current.UserTookOver || !string.Equals(current.ActiveAccountId, accountId, StringComparison.Ordinal))
                    return false;
            }
            if (!_accounts.TryResolveLiveAccount(accountId, out var live)) return false;
            if (_embeddings.TrackedRootFor(accountId) is not null && _embeddings.RootFor(accountId) is null)
                return false;
            if (!IsValidatedRoot(root, live) || !IsWindowVisible(root) || GetForegroundWindow() != root)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, session))
                        current.UserTookOver = GetForegroundWindow() != root;
                }
                return false;
            }
            return live.ProcessId == expected.ProcessId &&
                   live.ProcessStartTimeUtcTicks == expected.ProcessStartTimeUtcTicks &&
                   (live.RootWindowHandle == nint.Zero || root == live.RootWindowHandle) &&
                   ResolveRoot(accountId, live) == root;
        }

        var result = await _injector.PostAsync(root, events, cancellationToken, TargetStillValid,
            releaseFallback: null, InputDeliveryIntent.Default, traceId).ConfigureAwait(false);
        return result with { DeliveryMode = "send-input-session", Verification = result.Accepted ? "guarded" : result.Verification };
    }

    public async Task<AutomationSessionResult> CloseAsync(
        string pluginId,
        string sessionId,
        bool restore,
        bool userInitiated,
        CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out session) || !string.Equals(session.PluginId, pluginId, StringComparison.Ordinal))
                return AutomationSessionResult.Fail("session-not-found", "The foreground automation session is not owned by this plugin.");
            if (session.ActiveAccountId is not null && session.LastRoot != nint.Zero && GetForegroundWindow() != session.LastRoot)
                session.UserTookOver = true;
            _sessions.Remove(sessionId);
        }

        try
        {
            var shouldRestore = restore && session.RestoreForeground && !userInitiated && !session.UserTookOver;
            var restored = !shouldRestore || RestoreSnapshot(session.Snapshot);
            return AutomationSessionResult.Ok(session.Id, session.Snapshot) with { Restored = restored };
        }
        finally
        {
            try { _lane.Release(); } catch (ObjectDisposedException) { }
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    public void MarkUserTakeover(string sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session)) session.UserTookOver = true;
        }
    }

    public async Task CloseAllForPluginAsync(string pluginId)
    {
        string[] sessions;
        lock (_gate) sessions = _sessions.Values.Where(session => session.PluginId == pluginId).Select(session => session.Id).ToArray();
        foreach (var sessionId in sessions)
            await CloseAsync(pluginId, sessionId, restore: true, userInitiated: false).ConfigureAwait(false);
    }

    private nint ResolveRoot(string accountId, ManagedAccountSnapshot account) =>
        _embeddings.RootFor(accountId) ?? (account.RootWindowHandle != nint.Zero ? account.RootWindowHandle : account.WindowHandle);

    private static bool IsValidatedRoot(nint root, ManagedAccountSnapshot account)
    {
        if (root == nint.Zero || !IsWindow(root) || GetAncestor(root, GaRoot) != root)
            return false;
        if (GetWindowThreadProcessId(root, out var processId) == 0 || processId != account.ProcessId)
            return false;
        try
        {
            using var process = Process.GetProcessById(account.ProcessId);
            return !process.HasExited &&
                   process.StartTime.ToUniversalTime().Ticks == account.ProcessStartTimeUtcTicks &&
                   string.Equals(process.ProcessName, "RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool RestoreSnapshot(ForegroundSnapshot snapshot)
    {
        if (snapshot.ForegroundWindow == nint.Zero || !IsWindow(snapshot.ForegroundWindow)) return false;
        if (snapshot.ForegroundProcessId > 0 &&
            (GetWindowThreadProcessId(snapshot.ForegroundWindow, out var pid) == 0 || pid != snapshot.ForegroundProcessId)) return false;
        if (snapshot.ForegroundProcessId > 0)
        {
            try
            {
                using var process = Process.GetProcessById(snapshot.ForegroundProcessId);
                if (process.HasExited || (snapshot.ForegroundProcessStartTimeUtcTicks > 0 &&
                    process.StartTime.ToUniversalTime().Ticks != snapshot.ForegroundProcessStartTimeUtcTicks)) return false;
                if (!string.IsNullOrWhiteSpace(snapshot.ForegroundProcessName) &&
                    !string.Equals(process.ProcessName, snapshot.ForegroundProcessName, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(snapshot.ForegroundExecutablePath) &&
                    !string.Equals(TryGetExecutablePath(process), snapshot.ForegroundExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                return false;
            }
        }
        if (!SetForegroundWindow(snapshot.ForegroundWindow)) return false;
        if (snapshot.VisibleAccountId is not null)
            _embeddings.ShowOnly(snapshot.VisibleAccountId);
        return GetForegroundWindow() == snapshot.ForegroundWindow;
    }

    public async ValueTask DisposeAsync()
    {
        string[] sessions;
        lock (_gate)
        {
            _disposed = true;
            sessions = _sessions.Values.Select(session => session.Id).ToArray();
        }
        foreach (var sessionId in sessions)
        {
            Session? session;
            lock (_gate) _sessions.TryGetValue(sessionId, out session);
            if (session is null) continue;
            await CloseAsync(session.PluginId, sessionId, restore: false, userInitiated: false).ConfigureAwait(false);
        }
        _lane.Dispose();
    }

    private sealed class Session(string id, string pluginId, IReadOnlyList<string> accountIds, ForegroundSnapshot snapshot, bool restoreForeground)
    {
        public string Id { get; } = id;
        public string PluginId { get; } = pluginId;
        public IReadOnlyList<string> AccountIds { get; } = accountIds;
        public ForegroundSnapshot Snapshot { get; } = snapshot;
        public bool RestoreForeground { get; } = restoreForeground;
        public string? ActiveAccountId { get; set; }
        public nint LastRoot { get; set; }
        public bool UserTookOver { get; set; }
    }

    public sealed record AutomationSessionResult(
        bool Accepted,
        string Code,
        string Message,
        string? SessionId = null,
        string? AccountId = null,
        nint RootWindow = default,
        bool Restored = false,
        ForegroundSnapshot? Snapshot = null)
    {
        public static AutomationSessionResult Ok(string id, ForegroundSnapshot snapshot) =>
            new(true, "ok", "Foreground automation session accepted.", id, Snapshot: snapshot);
        public static AutomationSessionResult Fail(string code, string message) => new(false, code, message);
    }

    public sealed record ForegroundSnapshot(
        nint ForegroundWindow,
        int ForegroundProcessId,
        long ForegroundProcessStartTimeUtcTicks,
        string? ForegroundProcessName,
        string? ForegroundExecutablePath,
        nint CursorX,
        nint CursorY,
        string? VisibleAccountId)
    {
        public static ForegroundSnapshot Capture(string? visibleAccountId)
        {
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var processId);
            long startTicks = 0;
            string? processName = null;
            string? executablePath = null;
            try
            {
                if (processId > 0)
                {
                    using var process = Process.GetProcessById((int)processId);
                    startTicks = process.StartTime.ToUniversalTime().Ticks;
                    processName = process.ProcessName;
                    executablePath = TryGetExecutablePath(process);
                }
            }
            catch { }
            var point = new POINT();
            GetCursorPos(ref point);
            return new(foreground, (int)processId, startTicks, processName, executablePath, point.X, point.Y, visibleAccountId);
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
    }

    private const uint GaRoot = 2;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(ref POINT point);
}

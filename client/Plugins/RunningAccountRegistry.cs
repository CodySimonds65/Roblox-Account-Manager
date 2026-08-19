using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using RobloxAltClient.Models;

namespace RobloxAltClient.Plugins;

public sealed class RunningAccountRegistry : IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningAccountRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stopping = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private uint _lastInputTick;
    private DateTime _lastInputUtc = DateTime.UtcNow;

    // Roblox can briefly destroy and recreate its render window while changing
    // graphics modes or leaving a game.  Keep a bounded grace period so that a
    // transient HWND loss is not mistaken for a dead process, while still
    // cleaning up clients that have been closed natively.
    private static readonly TimeSpan MissingWindowGracePeriod = TimeSpan.FromSeconds(10);

    public event EventHandler<ManagedAccountSnapshot>? AccountChanged;
    public event EventHandler<ManagedAccountSnapshot>? AccountExited;
    public event EventHandler<string>? Diagnostic;

    public RunningAccountRegistry(string? appDataDirectory = null)
    {
        var root = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "running-accounts.json");
        Load();
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public IReadOnlyList<ManagedAccountSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _records.Values.Select(record => record.ToSnapshot()).ToArray();
        }
    }

    public void Register(AccountProfile account, Process process)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(process);
        process.Refresh();
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        var executablePath = TryGetExecutablePath(process) ?? string.Empty;
        lock (_gate)
        {
            _stopping.Remove(account.Id);
            _records[account.Id] = new RunningAccountRecord(account.Id, account.Label, process.Id, startTicks, DateTime.UtcNow,
                ExecutablePath: executablePath);
            SaveLocked();
        }
        AttachProcessWatcher(account.Id, process.Id, startTicks);
        Refresh();
        var registered = Snapshot().FirstOrDefault(snapshot => string.Equals(snapshot.AccountId, account.Id, StringComparison.Ordinal));
        if (registered is not null) AccountChanged?.Invoke(this, registered);
    }

    public bool Remove(string accountId)
    {
        ManagedAccountSnapshot exitedSnapshot;
        Process? wrapper;
        lock (_gate)
        {
            _processes.Remove(accountId, out wrapper);
            if (!_records.Remove(accountId, out var record)) return false;
            exitedSnapshot = record.ToSnapshot() with { IsRunning = false };
            SaveLocked();
        }
        DetachProcessWatcher(accountId, wrapper);
        RaiseAccountExited(exitedSnapshot);
        return true;
    }

    public bool IsStopping(string accountId)
    {
        lock (_gate) return _stopping.Contains(accountId);
    }

    /// <summary>
    /// Stops one managed Roblox process without ever acting on a reused PID.
    /// Watcher callbacks may win the race and publish the exit event once.
    /// </summary>
    public async Task<bool> TerminateAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        RunningAccountRecord? expected;
        lock (_gate)
        {
            if (!_records.TryGetValue(accountId, out expected)) return false;
            if (!_stopping.Add(accountId)) return false;
        }

        try
        {
            using var process = TryGetValidatedProcess(expected);
            if (process is null)
            {
                // The process disappeared or no longer matches the persisted
                // identity. Drop only our stale record; never kill a reused PID.
                FinalizeRecord(accountId, expected, exitCode: null,
                    diagnostic: $"Account {expected.Label} was no longer the managed process; its stale record was removed.");
                return true;
            }

            TryRequestGracefulClose(process);
            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var revalidated = TryGetValidatedProcess(expected);
                if (revalidated is not null && !revalidated.HasExited)
                {
                    try
                    {
                        revalidated.Kill(entireProcessTree: true);
                        await WaitForExitAsync(revalidated, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // A watcher may have observed exit between validation
                        // and Kill. Finalization below remains idempotent.
                    }
                    catch (Win32Exception ex)
                    {
                        Diagnostic?.Invoke(this, $"Could not terminate {expected.Label} (PID {expected.ProcessId}): {ex.Message}");
                    }
                }
            }

            var exited = HasExitedSafely(process);
            if (exited)
            {
                FinalizeRecord(accountId, expected, TryGetExitCode(process),
                    $"Account {expected.Label} (PID {expected.ProcessId}) was terminated by RAM.");
            }
            else
            {
                Diagnostic?.Invoke(this, $"RAM requested termination for {expected.Label}, but the process is still running.");
            }
            return exited;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            Diagnostic?.Invoke(this, $"Could not terminate {expected.Label} (PID {expected.ProcessId}): {ex.Message}");
            return false;
        }
        finally
        {
            lock (_gate) _stopping.Remove(accountId);
        }
    }

    /// <summary>Terminates every currently managed account, preserving unrelated Roblox processes.</summary>
    public async Task TerminateAllManagedAccountsAsync(CancellationToken cancellationToken = default)
    {
        string[] accountIds;
        lock (_gate) accountIds = _records.Keys.ToArray();
        if (accountIds.Length == 0) return;
        await Task.WhenAll(accountIds.Select(accountId => TerminateAccountAsync(accountId, cancellationToken))).ConfigureAwait(false);
    }

    private void AttachProcessWatcher(string accountId, int processId, long expectedStartTimeUtcTicks)
    {
        Process? wrapper;
        try
        {
            wrapper = Process.GetProcessById(processId);
            // Never watch a PID that belongs to a different process: persisted
            // records can be stale after PID reuse, and a watcher on the wrong
            // process would emit spurious exit events with misleading codes.
            if (wrapper.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
            {
                wrapper.Dispose();
                return;
            }
            wrapper.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return;
        }

        Process? previous;
        lock (_gate)
        {
            _processes.Remove(accountId, out previous);
            _processes[accountId] = wrapper;
        }

        wrapper.Exited += (_, _) => OnWatchedProcessExited(accountId, wrapper);
        if (previous is not null) DetachProcessWatcher(accountId, previous);
    }

    private static void DetachProcessWatcher(string accountId, Process? wrapper)
    {
        if (wrapper is null) return;
        try
        {
            wrapper.EnableRaisingEvents = false;
        }
        catch
        {
            // Best effort.
        }
        try
        {
            wrapper.Dispose();
        }
        catch
        {
            // Best effort: the process may already be finalized.
        }
    }

    private void OnWatchedProcessExited(string accountId, Process wrapper)
    {
        int? exitCode = null;
        try
        {
            if (wrapper.HasExited) exitCode = wrapper.ExitCode;
        }
        catch
        {
            // Exit code may be unavailable after the OS reaps the process.
        }

        ManagedAccountSnapshot? exitedSnapshot = null;
        var stale = false;
        lock (_gate)
        {
            // A watcher that was swapped out by a re-registration can fire late.
            // Only the CURRENT wrapper may remove the record; a stale one must
            // never touch a record that now belongs to a newer process.
            if (!_processes.TryGetValue(accountId, out var current) || !ReferenceEquals(current, wrapper))
            {
                stale = true;
            }
            else
            {
                _processes.Remove(accountId);
                if (_records.Remove(accountId, out var record))
                {
                    exitedSnapshot = record.ToSnapshot() with { IsRunning = false, ExitCode = exitCode };
                    SaveLocked();
                }
            }
        }

        if (stale)
        {
            DetachProcessWatcher(accountId, wrapper);
            return;
        }

        try { wrapper.Dispose(); } catch { }

        if (exitedSnapshot is not null)
        {
            Diagnostic?.Invoke(this, exitCode is null
                ? $"Account {exitedSnapshot.Label} (PID {exitedSnapshot.ProcessId}) exited; the process is no longer available."
                : $"Account {exitedSnapshot.Label} (PID {exitedSnapshot.ProcessId}) exited with code 0x{unchecked((uint)exitCode.Value):X8}.");
            RaiseAccountExited(exitedSnapshot);
        }
    }

    private void Refresh()
    {
        var foreground = GetForegroundWindow();
        var input = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref input) && input.dwTime != _lastInputTick)
        {
            _lastInputTick = input.dwTime;
            _lastInputUtc = DateTime.UtcNow;
        }

        List<ManagedAccountSnapshot> changed = [];
        List<ManagedAccountSnapshot> exited = [];
        List<Process> wrappersToDetach = [];
        List<string> missingWindowAccounts = [];
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var record in _records.Values.ToArray())
            {
                try
                {
                    var previousSnapshot = record.ToSnapshot();
                    using var process = Process.GetProcessById(record.ProcessId);
                    process.Refresh();
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != record.ProcessStartTimeUtcTicks ||
                        !MatchesExecutable(process, record))
                    {
                        _records.Remove(record.AccountId);
                        _processes.Remove(record.AccountId, out var wrapper);
                        if (wrapper is not null) wrappersToDetach.Add(wrapper);
                        int? exitCode = null;
                        try
                        {
                            if (process.StartTime.ToUniversalTime().Ticks == record.ProcessStartTimeUtcTicks)
                            {
                                if (wrapper is { HasExited: true }) exitCode = wrapper.ExitCode;
                                else if (process.HasExited) exitCode = process.ExitCode;
                            }
                        }
                        catch { }
                        exited.Add(record.ToSnapshot() with { IsRunning = false, ExitCode = exitCode });
                        Diagnostic?.Invoke(this, exitCode is null
                            ? $"Account {record.Label} (PID {record.ProcessId}) exited; the process is no longer available."
                            : $"Account {record.Label} (PID {record.ProcessId}) exited with code 0x{unchecked((uint)exitCode.Value):X8}.");
                        continue;
                    }

                    var previousWindow = (nint)record.WindowHandle;
                    var hwnd = IsOwnedProcessWindow(previousWindow, process.Id)
                        ? previousWindow
                        : FindWindow(process.Id);
                    DateTime? missingSince = hwnd == nint.Zero
                        ? record.MissingWindowSinceUtc ?? now
                        : null;
                    var snapshot = record with
                    {
                        WindowHandle = hwnd.ToInt64(),
                        MissingWindowSinceUtc = missingSince,
                        LastActivityUtc = hwnd != nint.Zero && (foreground == hwnd || GetAncestor(foreground, GA_ROOT) == GetAncestor(hwnd, GA_ROOT)) ? _lastInputUtc : record.LastActivityUtc
                    };
                    if (hwnd == nint.Zero && missingSince is not null && now - missingSince.Value >= MissingWindowGracePeriod && !_stopping.Contains(record.AccountId))
                        missingWindowAccounts.Add(record.AccountId);
                    if (snapshot != record)
                    {
                        _records[record.AccountId] = snapshot;
                        var currentSnapshot = snapshot.ToSnapshot();
                        if (currentSnapshot != previousSnapshot) changed.Add(currentSnapshot);
                    }
                    else
                    {
                        var currentSnapshot = record.ToSnapshot();
                        if (currentSnapshot != previousSnapshot) changed.Add(currentSnapshot);
                    }
                }
                catch (ArgumentException)
                {
                    _processes.Remove(record.AccountId, out var wrapper);
                    if (wrapper is not null) wrappersToDetach.Add(wrapper);
                    _records.Remove(record.AccountId);
                    exited.Add(record.ToSnapshot() with { IsRunning = false });
                    Diagnostic?.Invoke(this, $"Account {record.Label} (PID {record.ProcessId}) exited; the process is no longer available.");
                }
                catch (InvalidOperationException)
                {
                    _processes.Remove(record.AccountId, out var wrapper);
                    if (wrapper is not null) wrappersToDetach.Add(wrapper);
                    _records.Remove(record.AccountId);
                    exited.Add(record.ToSnapshot() with { IsRunning = false });
                    Diagnostic?.Invoke(this, $"Account {record.Label} (PID {record.ProcessId}) exited; the process is no longer available.");
                }
                catch (Win32Exception)
                {
                    _processes.Remove(record.AccountId, out var wrapper);
                    if (wrapper is not null) wrappersToDetach.Add(wrapper);
                    _records.Remove(record.AccountId);
                    exited.Add(record.ToSnapshot() with { IsRunning = false });
                    Diagnostic?.Invoke(this, $"Account {record.Label} (PID {record.ProcessId}) exited; the process is no longer available.");
                }
            }

            if (exited.Count > 0 || changed.Count > 0) SaveLocked();
        }

        foreach (var wrapper in wrappersToDetach) DetachProcessWatcher(string.Empty, wrapper);

        foreach (var snapshot in changed) AccountChanged?.Invoke(this, snapshot);
        foreach (var snapshot in exited) RaiseAccountExited(snapshot);
        foreach (var accountId in missingWindowAccounts)
            _ = TerminateAccountAsync(accountId);
    }

    private void RaiseAccountExited(ManagedAccountSnapshot snapshot)
    {
        var handlers = AccountExited?.GetInvocationList() ?? [];
        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<ManagedAccountSnapshot>)handler)(this, snapshot);
            }
            catch (Exception ex)
            {
                // A broken subscriber must never crash the registry thread.
                Diagnostic?.Invoke(this, $"Account-exit handler failed for {snapshot.Label}: {ex.Message}");
            }
        }
    }

    private static nint FindWindow(int processId)
    {
        nint candidate = nint.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var ownerPid);
            if (ownerPid != processId || !IsWindowVisible(hwnd) || IsWindow(hwnd) == false) return true;
            if (GetWindow(hwnd, GW_OWNER) != nint.Zero) return true;
            candidate = FindRenderChild(hwnd);
            if (candidate == nint.Zero) candidate = hwnd;
            return false;
        }, nint.Zero);
        return candidate;
    }

    private static nint FindRenderChild(nint root)
    {
        nint selected = nint.Zero; var selectedArea = 0L;
        EnumChildWindows(root, (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || !GetClientRect(hwnd, out var rect)) return true;
            var className = new char[128]; var length = GetClassName(hwnd, ref className[0], className.Length);
            var name = length > 0 ? new string(className, 0, length) : string.Empty;
            if (!name.Contains("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Roblox", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("SDL_app", StringComparison.OrdinalIgnoreCase)) return true;
            var area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (area > selectedArea) { selected = hwnd; selectedArea = area; }
            return true;
        }, nint.Zero);
        return selected;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var records = JsonSerializer.Deserialize<List<RunningAccountRecord>>(File.ReadAllText(_path), PluginJson.Options);
            if (records is null) return;
            foreach (var record in records)
            {
                _records[record.AccountId] = record;
                AttachProcessWatcher(record.AccountId, record.ProcessId, record.ProcessStartTimeUtcTicks);
            }
        }
        catch
        {
            _records.Clear();
        }
    }

    private void SaveLocked()
    {
        var temporaryPath = _path + ".tmp";
        try
        {
            // Persist HWNDs as Int64 values. System.Text.Json's reflection
            // metadata cannot reliably construct records with nint parameters
            // on all supported .NET 8 Windows runtimes.
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_records.Values.OrderBy(record => record.AccountId), PluginJson.Options));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            Diagnostic?.Invoke(this, $"Running-account state was not persisted: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            foreach (var wrapper in _processes.Values)
            {
                try { wrapper.EnableRaisingEvents = false; } catch { }
                try { wrapper.Dispose(); } catch { }
            }
            _processes.Clear();
        }
    }

    private sealed record RunningAccountRecord(
        string AccountId,
        string Label,
        int ProcessId,
        long ProcessStartTimeUtcTicks,
        DateTime LastActivityUtc,
        long WindowHandle = 0,
        string ExecutablePath = "",
        DateTime? MissingWindowSinceUtc = null)
    {
        public ManagedAccountSnapshot ToSnapshot()
        {
            var windowHandle = (nint)WindowHandle;
            var rect = GetClientMetrics(windowHandle);
            var processRoot = GetProcessRootWindow(windowHandle, ProcessId);
            return new ManagedAccountSnapshot(
                AccountId,
                Label,
                ProcessId,
                ProcessStartTimeUtcTicks,
                windowHandle,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                windowHandle == nint.Zero ? 96u : GetDpiForWindow(windowHandle),
                windowHandle != nint.Zero && IsIconic(GetAncestor(windowHandle, GA_ROOT)),
                LastActivityUtc,
                windowHandle != nint.Zero,
                processRoot);
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Process? TryGetValidatedProcess(RunningAccountRecord expected)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(expected.ProcessId);
            process.Refresh();
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != expected.ProcessStartTimeUtcTicks ||
                !MatchesExecutable(process, expected))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            process?.Dispose();
            return null;
        }
    }

    private static bool MatchesExecutable(Process process, RunningAccountRecord expected)
    {
        var currentPath = TryGetExecutablePath(process);
        if (!string.IsNullOrWhiteSpace(expected.ExecutablePath))
        {
            try
            {
                return currentPath is not null &&
                       string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(expected.ExecutablePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // Older persisted records predate executable-path tracking.  Refuse to
        // act on an unknown process unless its stable image name is Roblox.
        return string.Equals(process.ProcessName, "RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryRequestGracefulClose(Process process)
    {
        try
        {
            process.Refresh();
            // CloseMainWindow is the only graceful-close path used here. It
            // respects the process's own window/message loop without adding a
            // second window-message input path outside the safety broker.
            _ = !process.HasExited && process.CloseMainWindow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Embedded clients do not always expose a conventional main window;
            // the validated force-termination path below remains available.
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExitedSafely(process)) return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return HasExitedSafely(process);
    }

    private static bool HasExitedSafely(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return true; }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
    }

    private void FinalizeRecord(string accountId, RunningAccountRecord expected, int? exitCode, string diagnostic)
    {
        ManagedAccountSnapshot? exitedSnapshot = null;
        Process? wrapper = null;
        lock (_gate)
        {
            if (_records.TryGetValue(accountId, out var current) &&
                current.ProcessId == expected.ProcessId &&
                current.ProcessStartTimeUtcTicks == expected.ProcessStartTimeUtcTicks)
            {
                _records.Remove(accountId);
                _processes.Remove(accountId, out wrapper);
                exitedSnapshot = current.ToSnapshot() with { IsRunning = false, ExitCode = exitCode };
                SaveLocked();
            }
        }

        DetachProcessWatcher(accountId, wrapper);
        Diagnostic?.Invoke(this, diagnostic);
        if (exitedSnapshot is not null) RaiseAccountExited(exitedSnapshot);
    }

    private static bool IsOwnedProcessWindow(nint hwnd, int processId)
    {
        if (hwnd == nint.Zero || !IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var ownerPid);
        return ownerPid == processId;
    }

    private static nint GetProcessRootWindow(nint hwnd, int processId)
    {
        if (!IsOwnedProcessWindow(hwnd, processId)) return nint.Zero;
        var current = hwnd;
        while (true)
        {
            var parent = GetParent(current);
            if (!IsOwnedProcessWindow(parent, processId)) return current;
            current = parent;
        }
    }

    private static (int X, int Y, int Width, int Height) GetClientMetrics(nint hwnd)
    {
        if (hwnd == nint.Zero || !GetClientRect(hwnd, out var client)) return default;
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        return (origin.X, origin.Y, Math.Max(0, client.Right - client.Left), Math.Max(0, client.Bottom - client.Top));
    }

    private const uint GW_OWNER = 4;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, ref char className, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    private struct POINT { public int X; public int Y; }
}

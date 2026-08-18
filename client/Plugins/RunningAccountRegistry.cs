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
    private readonly Timer _timer;
    private uint _lastInputTick;
    private DateTime _lastInputUtc = DateTime.UtcNow;

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
        lock (_gate)
        {
            _records[account.Id] = new RunningAccountRecord(account.Id, account.Label, process.Id, startTicks, DateTime.UtcNow);
            SaveLocked();
        }
        Refresh();
        var registered = Snapshot().FirstOrDefault(snapshot => string.Equals(snapshot.AccountId, account.Id, StringComparison.Ordinal));
        if (registered is not null) AccountChanged?.Invoke(this, registered);
    }

    public bool Remove(string accountId)
    {
        ManagedAccountSnapshot exitedSnapshot;
        lock (_gate)
        {
            if (!_records.Remove(accountId, out var record)) return false;
            exitedSnapshot = record.ToSnapshot() with { IsRunning = false };
            SaveLocked();
        }
        AccountExited?.Invoke(this, exitedSnapshot);
        return true;
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
        lock (_gate)
        {
            foreach (var record in _records.Values.ToArray())
            {
                try
                {
                    var previousSnapshot = record.ToSnapshot();
                    using var process = Process.GetProcessById(record.ProcessId);
                    process.Refresh();
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != record.ProcessStartTimeUtcTicks)
                    {
                        _records.Remove(record.AccountId);
                        exited.Add(record.ToSnapshot() with { IsRunning = false });
                        continue;
                    }

                    var hwnd = FindWindow(process.Id);
                    var snapshot = record with
                    {
                        WindowHandle = hwnd.ToInt64(),
                        LastActivityUtc = hwnd != nint.Zero && (foreground == hwnd || GetAncestor(foreground, GA_ROOT) == GetAncestor(hwnd, GA_ROOT)) ? _lastInputUtc : record.LastActivityUtc
                    };
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
                    _records.Remove(record.AccountId);
                    exited.Add(record.ToSnapshot() with { IsRunning = false });
                }
                catch (InvalidOperationException)
                {
                    _records.Remove(record.AccountId);
                    exited.Add(record.ToSnapshot() with { IsRunning = false });
                }
                catch (Win32Exception)
                {
                    _records.Remove(record.AccountId);
                    exited.Add(record.ToSnapshot() with { IsRunning = false });
                }
            }

            if (exited.Count > 0) SaveLocked();
        }

        foreach (var snapshot in changed) AccountChanged?.Invoke(this, snapshot);
        foreach (var snapshot in exited) AccountExited?.Invoke(this, snapshot);
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
            foreach (var record in records) _records[record.AccountId] = record;
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
        catch (Exception ex) when (ex is InvalidCastException or JsonException or IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            Diagnostic?.Invoke(this, $"Running-account state was not persisted: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();

    private sealed record RunningAccountRecord(
        string AccountId,
        string Label,
        int ProcessId,
        long ProcessStartTimeUtcTicks,
        DateTime LastActivityUtc,
        long WindowHandle = 0)
    {
        public ManagedAccountSnapshot ToSnapshot()
        {
            var windowHandle = (nint)WindowHandle;
            var rect = GetClientMetrics(windowHandle);
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
                windowHandle == nint.Zero ? nint.Zero : GetAncestor(windowHandle, GA_ROOT));
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

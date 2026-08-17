using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RobloxAltClient.Plugins;

public sealed class PluginProcessSupervisor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _suspendedThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokenFiles = new(StringComparer.Ordinal);

    public event EventHandler<(string PluginId, int ProcessId, long ProcessStartTimeUtcTicks)>? Exited;

    public int Start(PluginManifest manifest, string executablePath, string pipeName, string token, string dataDirectory)
    {
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Plugin entrypoint is missing.", executablePath);
        Directory.CreateDirectory(dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, ".launch-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tokenPath, token, new UTF8Encoding(false));
        var arguments = $"--ram-plugin --pipe \"{pipeName}\" --token-file \"{tokenPath}\" --plugin-id \"{manifest.Id}\" --data \"{dataDirectory}\"";
        var job = CreateJobObject(nint.Zero, null);
        if (job == nint.Zero || !ConfigureKillOnClose(job))
        {
            if (job != nint.Zero) CloseHandle(job);
            throw new InvalidOperationException("The plugin process job could not be created.");
        }
        Process process;
        try
        {
            var suspended = MediumIntegrityProcessStarter.StartSuspended(executablePath, arguments, Path.GetDirectoryName(executablePath)!, job);
            process = suspended.Process;
            lock (_gate) { _suspendedThreads[manifest.Id] = suspended.ThreadHandle; _tokenFiles[manifest.Id] = tokenPath; }
        }
        catch
        {
            try { File.Delete(tokenPath); } catch { }
            CloseHandle(job);
            throw;
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(manifest.Id, process);
        lock (_gate)
        {
            if (_processes.TryGetValue(manifest.Id, out var old))
            {
                try { if (!old.HasExited) old.Kill(entireProcessTree: true); } catch { }
                old.Dispose();
                if (_jobs.Remove(manifest.Id, out var oldJob) && oldJob != nint.Zero) CloseHandle(oldJob);
                if (_tokenFiles.Remove(manifest.Id, out var oldToken)) try { File.Delete(oldToken); } catch { }
            }
            _processes[manifest.Id] = process;
            _jobs[manifest.Id] = job;
        }
        return process.Id;
    }

    public void Resume(string pluginId)
    {
        nint thread;
        lock (_gate)
        {
            if (!_suspendedThreads.Remove(pluginId, out thread)) throw new InvalidOperationException("Plugin process is not awaiting authentication binding.");
        }
        if (ResumeThread(thread) == uint.MaxValue)
        {
            CloseHandle(thread);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the authenticated plugin process.");
        }
        CloseHandle(thread);
    }

    public async Task StopAsync(string pluginId)
    {
        Process? process;
        nint job;
        nint suspendedThread;
        string? tokenFile;
        lock (_gate)
        {
            _processes.Remove(pluginId, out process);
            _jobs.Remove(pluginId, out job);
            _suspendedThreads.Remove(pluginId, out suspendedThread);
            _tokenFiles.Remove(pluginId, out tokenFile);
        }
        if (tokenFile is not null) try { File.Delete(tokenFile); } catch { }
        if (suspendedThread != nint.Zero) CloseHandle(suspendedThread);
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.Dispose();
        }
        if (job != nint.Zero) CloseHandle(job);
    }

    public Task StopAllAsync()
    {
        string[] ids;
        lock (_gate) ids = _processes.Keys.ToArray();
        return Task.WhenAll(ids.Select(StopAsync));
    }

    private void OnExited(string pluginId, Process process)
    {
        nint suspendedThread = nint.Zero;
        var notify = false;
        long startTicks = 0;
        lock (_gate)
        {
            if (_processes.TryGetValue(pluginId, out var current) && ReferenceEquals(current, process))
            {
                try { startTicks = process.StartTime.ToUniversalTime().Ticks; } catch { }
                _processes.Remove(pluginId);
                if (_jobs.Remove(pluginId, out var job) && job != nint.Zero) CloseHandle(job);
                _suspendedThreads.Remove(pluginId, out suspendedThread);
                _tokenFiles.Remove(pluginId, out var tokenFile);
                if (tokenFile is not null) try { File.Delete(tokenFile); } catch { }
                notify = true;
            }
        }
        if (suspendedThread != nint.Zero) CloseHandle(suspendedThread);
        if (notify) Exited?.Invoke(this, (pluginId, process.Id, startTicks));
        process.Dispose();
    }

    public void Dispose()
    {
        Process[] processes;
        nint[] jobs;
        lock (_gate)
        {
            processes = _processes.Values.ToArray();
            jobs = _jobs.Values.ToArray();
            foreach (var thread in _suspendedThreads.Values) if (thread != nint.Zero) CloseHandle(thread);
            _suspendedThreads.Clear();
            foreach (var tokenFile in _tokenFiles.Values) try { File.Delete(tokenFile); } catch { }
            _tokenFiles.Clear();
            _jobs.Clear();
            _processes.Clear();
        }
        foreach (var process in processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        foreach (var job in jobs) if (job != nint.Zero) CloseHandle(job);
    }

    private static bool ConfigureKillOnClose(nint job)
    {
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
        };
        return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
    }

    [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS
    {
        public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount;
        public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount;
    }
}

internal static class MediumIntegrityProcessStarter
{
    public static SuspendedProcess StartSuspended(string executablePath, string arguments, string workingDirectory, nint job)
    {
        var shell = GetShellWindow();
        if (shell == nint.Zero) throw new InvalidOperationException("Could not find the interactive Windows shell.");
        GetWindowThreadProcessId(shell, out var shellPid);
        var shellProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (shellProcess == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the shell process.");
        try
        {
            if (!OpenProcessToken(shellProcess, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, out var sourceToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the shell token.");
            try
            {
                if (!DuplicateTokenEx(sourceToken, TOKEN_ALL_ACCESS, nint.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                        TOKEN_TYPE.TokenPrimary, out var primaryToken))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the shell token.");
                try
                {
                    var commandLine = new StringBuilder($"\"{executablePath}\" {arguments}");
                    var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\default" };
                    if (!CreateProcessWithTokenW(primaryToken, LOGON_WITH_PROFILE, null, commandLine, CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                            nint.Zero, workingDirectory, ref startup, out var info))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not launch the plugin at medium integrity.");
                    var handedOffThread = false;
                    try
                    {
                        if (!AssignProcessToJobObject(job, info.ProcessHandle))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the plugin to its job object.");
                        var process = Process.GetProcessById((int)info.ProcessId);
                        handedOffThread = true;
                        return new SuspendedProcess(process, info.ThreadHandle);
                    }
                    catch
                    {
                        TerminateProcess(info.ProcessHandle, 1);
                        throw;
                    }
                    finally
                    {
                        if (info.ThreadHandle != nint.Zero && !handedOffThread) CloseHandle(info.ThreadHandle);
                        CloseHandle(info.ProcessHandle);
                    }
                }
                finally { CloseHandle(primaryToken); }
            }
            finally { CloseHandle(sourceToken); }
        }
        finally { CloseHandle(shellProcess); }
    }

    public sealed record SuspendedProcess(Process Process, nint ThreadHandle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint LOGON_WITH_PROFILE = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_SUSPENDED = 0x00000004;

    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint tokenAttributes, SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out nint primaryToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? applicationName, StringBuilder commandLine, uint creationFlags, nint environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint exitCode);

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation = 2 }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public nint lpReserved2; public nint hStdInput; public nint hStdOutput; public nint hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public nint ProcessHandle; public nint ThreadHandle; public uint ProcessId; public uint ThreadId; }
}

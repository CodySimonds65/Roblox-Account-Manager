using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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
        MediumIntegrityLabel.Apply(dataDirectory, isDirectory: true);
        var tokenPath = Path.Combine(dataDirectory, ".launch-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(tokenPath, token, new UTF8Encoding(false));
        MediumIntegrityLabel.Apply(tokenPath, isDirectory: false);
        var tokenOwnedBySupervisor = false;
        try
        {
        var arguments = $"--ram-plugin --pipe \"{pipeName}\" --token-file \"{tokenPath}\" --plugin-id \"{manifest.Id}\" --data \"{dataDirectory}\"";
        var job = CreateJobObject(nint.Zero, null);
        if (job == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The plugin process job could not be created.");
        }
        if (!ConfigureKillOnClose(job))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(job);
            throw new Win32Exception(error, "The plugin process job could not be configured.");
        }
        Process process;
        try
        {
            var suspended = MediumIntegrityProcessStarter.StartSuspended(executablePath, arguments, Path.GetDirectoryName(executablePath)!, job);
            process = suspended.Process;
            lock (_gate) { _suspendedThreads[manifest.Id] = suspended.ThreadHandle; _tokenFiles[manifest.Id] = tokenPath; }
            tokenOwnedBySupervisor = true;
        }
        catch
        {
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
        finally
        {
            if (!tokenOwnedBySupervisor) try { File.Delete(tokenPath); } catch { }
        }
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
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
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

internal static class MediumIntegrityLabel
{
    private const string LabelSddl = "S:(ML;;NWNX;;;ME)";
    private const byte MandatoryLabelAceType = 0x11;
    private static int _securityPrivilegeEnabled;

    public static void Apply(string path, bool isDirectory)
    {
        try
        {
            if (!TryEnableSecurityPrivilege()) return;
            FileSystemSecurity accessControl = isDirectory
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            var labeledSd = AddMediumIntegrityLabel(accessControl.GetSecurityDescriptorBinaryForm());
            if (!SetFileSecurityW(path, DaclSecurityInformation | SaclSecurityInformation, labeledSd))
            {
                // Labeling is best-effort; the token handshake remains the security
                // boundary. An unlabeled path still works for today's plugins.
            }
        }
        catch
        {
            // Best-effort; see Apply. A failed label must not block the launch.
        }
    }

    internal static byte[] AddMediumIntegrityLabel(byte[] existingSecurityDescriptorBinaryForm)
    {
        var descriptor = new RawSecurityDescriptor(existingSecurityDescriptorBinaryForm, 0);
        if (descriptor.SystemAcl is not null)
            foreach (var ace in descriptor.SystemAcl)
                if ((byte)ace.AceType == MandatoryLabelAceType)
                    return existingSecurityDescriptorBinaryForm;
        var labelAce = new RawSecurityDescriptor(LabelSddl).SystemAcl![0];
        if (descriptor.SystemAcl is null)
        {
            descriptor.SystemAcl = new RawAcl(GenericAcl.AclRevision, 1);
            descriptor.SystemAcl.InsertAce(0, labelAce);
        }
        else
        {
            descriptor.SystemAcl.InsertAce(descriptor.SystemAcl.Count, labelAce);
        }
        return SerializeDescriptor(descriptor);
    }

    private static byte[] SerializeDescriptor(RawSecurityDescriptor descriptor)
    {
        var ownerBytes = descriptor.Owner is null ? null : GetBinary(descriptor.Owner);
        var groupBytes = descriptor.Group is null ? null : GetBinary(descriptor.Group);
        var saclBytes = descriptor.SystemAcl is null ? null : GetBinary(descriptor.SystemAcl);
        var daclBytes = descriptor.DiscretionaryAcl is null ? null : GetBinary(descriptor.DiscretionaryAcl);
        var buffer = new byte[20 + (ownerBytes?.Length ?? 0) + (groupBytes?.Length ?? 0) + (saclBytes?.Length ?? 0) + (daclBytes?.Length ?? 0)];
        buffer[0] = 1;
        WriteUInt16(buffer, 2, (ushort)((ushort)descriptor.ControlFlags | (ushort)ControlFlags.SystemAclPresent));
        var offset = 20;
        if (ownerBytes is not null) { WriteInt32(buffer, 4, offset); ownerBytes.CopyTo(buffer, offset); offset += ownerBytes.Length; }
        if (groupBytes is not null) { WriteInt32(buffer, 8, offset); groupBytes.CopyTo(buffer, offset); offset += groupBytes.Length; }
        if (saclBytes is not null) { WriteInt32(buffer, 12, offset); saclBytes.CopyTo(buffer, offset); offset += saclBytes.Length; }
        if (daclBytes is not null) { WriteInt32(buffer, 16, offset); daclBytes.CopyTo(buffer, offset); }
        return buffer;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static byte[] GetBinary(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static byte[] GetBinary(GenericAcl acl)
    {
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static bool TryEnableSecurityPrivilege()
    {
        if (Volatile.Read(ref _securityPrivilegeEnabled) == 1) return true;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var tokenHandle)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, SeSecurityPrivilegeName, out var luid)) return false;
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, nint.Zero, nint.Zero)) return false;
                if (Marshal.GetLastWin32Error() == (int)WinError.NotAllAssigned) return false;
                Volatile.Write(ref _securityPrivilegeEnabled, 1);
                return true;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch
        {
            return false;
        }
    }

    private const string SeSecurityPrivilegeName = "SeSecurityPrivilege";
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;
    private const uint DaclSecurityInformation = 0x4;
    private const uint SaclSecurityInformation = 0x8;

    private enum WinError
    {
        NotAllAssigned = 0x514
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(nint tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetFileSecurityW(string fileName, uint securityInformation, byte[] securityDescriptor);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
}

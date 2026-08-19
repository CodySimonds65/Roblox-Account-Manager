using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAltClient.Plugins;

/// <summary>
/// Reads the Windows mandatory-integrity label without changing process
/// privileges. This lets guarded input report a real UIPI mismatch instead of
/// presenting every injection failure as a generic unavailable target.
/// </summary>
internal enum ProcessIntegrityLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    System = 4,
    Protected = 5
}

internal static class ProcessIntegrity
{
    public static ProcessIntegrityLevel Current
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return ForProcess(process);
        }
    }

    public static ProcessIntegrityLevel ForWindow(nint window)
    {
        if (window == nint.Zero || !IsWindow(window)) return ProcessIntegrityLevel.Unknown;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return ProcessIntegrityLevel.Unknown;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return ForProcess(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ProcessIntegrityLevel.Unknown;
        }
    }

    public static ProcessIntegrityLevel ForProcess(Process process)
    {
        nint processHandle = nint.Zero;
        nint tokenHandle = nint.Zero;
        nint buffer = nint.Zero;
        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (processHandle == nint.Zero || !OpenProcessToken(processHandle, TokenQuery, out tokenHandle))
                return ProcessIntegrityLevel.Unknown;

            _ = GetTokenInformation(tokenHandle, TokenIntegrityLevel, nint.Zero, 0, out var length);
            if (length == 0) return ProcessIntegrityLevel.Unknown;
            buffer = Marshal.AllocHGlobal((int)length);
            if (!GetTokenInformation(tokenHandle, TokenIntegrityLevel, buffer, length, out _))
                return ProcessIntegrityLevel.Unknown;

            var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
            if (label.Label.Sid == nint.Zero) return ProcessIntegrityLevel.Unknown;
            var subAuthorityCount = Marshal.ReadByte(label.Label.Sid, 1);
            if (subAuthorityCount == 0) return ProcessIntegrityLevel.Unknown;
            var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(subAuthorityCount - 1));
            var rid = Marshal.ReadInt32(ridPointer);
            return rid switch
            {
                >= 0x5000 => ProcessIntegrityLevel.Protected,
                >= 0x4000 => ProcessIntegrityLevel.System,
                >= 0x3000 => ProcessIntegrityLevel.High,
                >= 0x2000 => ProcessIntegrityLevel.Medium,
                _ => ProcessIntegrityLevel.Low
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A protected process may deny token inspection. Unknown is safer
            // than assuming it is injectable.
        }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
            if (tokenHandle != nint.Zero) CloseHandle(tokenHandle);
            if (processHandle != nint.Zero) CloseHandle(processHandle);
        }
        return ProcessIntegrityLevel.Unknown;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint processHandle, uint access, out nint tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint tokenHandle, int informationClass, nint information, uint informationLength, out uint returnLength);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint subAuthorityIndex);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

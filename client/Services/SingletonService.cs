using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace RobloxAltClient.Services;

public sealed partial class SingletonService
{
    private static readonly Uri HandleDownloadUri = new("https://download.sysinternals.com/files/Handle.zip");
    private static readonly HttpClient HttpClient = new();

    public async Task<UnlockResult> ReleaseAsync()
    {
        if (!IsAdministrator())
        {
            return new UnlockResult(
                false,
                0,
                ["Roblox Account Manager is not running as administrator. Close it and start it again."]);
        }

        try
        {
            var handlePath = await PrepareHandleToolAsync();
            return await ReleaseHandlesAsync(handlePath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            return new UnlockResult(false, 0, [exception.Message]);
        }
    }

    public async Task<SingletonSessionStartResult> StartSessionAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAdministrator())
            {
                return new SingletonSessionStartResult(
                    false,
                    null,
                    ["Roblox Account Manager is not running as administrator. Close it and start it again."]);
            }

            var handlePath = await PrepareHandleToolAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new SingletonSessionStartResult(
                true,
                new SingletonUnlockSession(handlePath),
                ["Using the client's existing elevated security context; no additional UAC prompt will be requested."]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SingletonSessionStartResult(
                false,
                null,
                [$"Could not prepare the native singleton unlock service: {exception.Message}"]);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<string> PrepareHandleToolAsync(CancellationToken cancellationToken = default)
    {
        var toolDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RobloxAltClient",
            "Tools");
        EnsureProtectedToolDirectory(toolDirectory);
        return await EnsureHandleToolAsync(Path.Combine(toolDirectory, "handle64.exe"), cancellationToken);
    }

    internal static async Task<UnlockResult> ReleaseHandlesAsync(
        string handlePath,
        CancellationToken cancellationToken,
        TimeSpan? settleWindow = null)
    {
        if (GetRunningRobloxProcessIdentities().Count == 0)
        {
            return new UnlockResult(
                false,
                0,
                ["No running Roblox client was found. Launch the first account into a game before preparing another account."]);
        }

        try
        {
            var coordinator = new SingletonHandleReleaseCoordinator();
            var quietWindow = settleWindow ?? TimeSpan.Zero;
            var sweep = await coordinator.ReleaseAsync(
                _ => Task.FromResult<IReadOnlyList<SingletonProcessIdentity>>(GetRunningRobloxProcessIdentities()),
                (process, token) => InspectSingletonHandlesAsync(handlePath, process, token),
                (process, handle, token) => CloseSingletonHandleAsync(handlePath, process, handle, token),
                maxPasses: quietWindow > TimeSpan.Zero ? 30 : 3,
                cancellationToken: cancellationToken,
                retryDelay: quietWindow > TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero,
                settleWindow: quietWindow);
            var messages = sweep.Messages.ToList();
            if (sweep.ClosedCount == 0)
            {
                messages.Add("No singleton handles are currently present; Roblox is already unlocked.");
            }

            return new UnlockResult(sweep.Success, sweep.ClosedCount, [.. messages]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UnlockResult(false, 0, [exception.Message]);
        }
    }

    private static IReadOnlyList<SingletonProcessIdentity> GetRunningRobloxProcessIdentities()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("RobloxPlayerBeta");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return [];
        }

        var identities = new List<SingletonProcessIdentity>();
        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                    continue;

                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !string.Equals(Path.GetFileName(executablePath), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                identities.Add(new SingletonProcessIdentity(
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    CanonicalizeExecutablePath(executablePath)));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                // The client exited while the process list was being read.
            }
            finally
            {
                process.Dispose();
            }
        }

        return identities;
    }

    private static async Task<IReadOnlyList<SingletonHandleInfo>> InspectSingletonHandlesAsync(
        string handlePath,
        SingletonProcessIdentity process,
        CancellationToken cancellationToken)
    {
        EnsureProcessIdentity(process);
        var result = await RunHandleAsync(
            handlePath,
            cancellationToken,
            "-accepteula", "-nobanner", "-a", "-p", process.Pid.ToString());
        try
        {
            EnsureHandleSucceeded(result, $"inspect Roblox PID {process.Pid}");
        }
        catch (InvalidOperationException) when (!HasProcessIdentity(process))
        {
            throw new SingletonProcessGoneException(process.Pid);
        }

        // Do not trust a PID after an external command returns: reject PID
        // reuse or a changed executable before parsing/using its handles.
        EnsureProcessIdentity(process);
        return ParseSingletonHandles(result.Output);
    }

    private static Task CloseSingletonHandleAsync(
        string handlePath,
        SingletonProcessIdentity process,
        SingletonHandleInfo handle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ulong.TryParse(handle.Id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handleValue))
            throw new InvalidOperationException($"Sysinternals returned an invalid handle ID for {handle.Name}.");

        // OpenProcess returns a handle to the process object itself. Keep that
        // native handle alive for the complete close operation so a recycled
        // PID can never redirect DUPLICATE_CLOSE_SOURCE to another process.
        var nativeProcess = OpenVerifiedProcessHandle(process);
        try
        {
            var sourceHandle = new IntPtr(unchecked((long)handleValue));
            VerifyRemoteHandle(nativeProcess, sourceHandle, handle);

            IntPtr duplicatedHandle = IntPtr.Zero;
            if (!DuplicateHandle(
                    nativeProcess,
                    sourceHandle,
                    GetCurrentProcess(),
                    out duplicatedHandle,
                    0,
                    false,
                    DuplicateSameAccess | DuplicateCloseSource))
            {
                var error = Marshal.GetLastWin32Error();
                if (!HasProcessIdentity(process))
                    throw new SingletonProcessGoneException(process.Pid);

                throw new InvalidOperationException(
                    $"Windows could not close {handle.Name} in Roblox PID {process.Pid} (native error {error}).");
            }

            if (duplicatedHandle != IntPtr.Zero)
                CloseHandle(duplicatedHandle);
        }
        finally
        {
            CloseHandle(nativeProcess);
        }

        return Task.CompletedTask;
    }

    private static IntPtr OpenVerifiedProcessHandle(SingletonProcessIdentity expected)
    {
        var processHandle = OpenProcess(
            ProcessDuplicateHandle | ProcessQueryLimitedInformation,
            false,
            expected.Pid);
        if (processHandle == IntPtr.Zero)
            throw new SingletonProcessGoneException(expected.Pid);

        try
        {
            VerifyNativeProcessIdentity(processHandle, expected);
            return processHandle;
        }
        catch
        {
            CloseHandle(processHandle);
            throw;
        }
    }

    private static void VerifyRemoteHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        SingletonHandleInfo expected)
    {
        IntPtr duplicatedHandle = IntPtr.Zero;
        if (!DuplicateHandle(
                sourceProcessHandle,
                sourceHandle,
                GetCurrentProcess(),
                out duplicatedHandle,
                0,
                false,
                DuplicateSameAccess))
        {
            throw new InvalidOperationException($"Could not verify {expected.Name} before closing it.");
        }

        try
        {
            if (!TryQueryObjectString(duplicatedHandle, ObjectTypeInformation, out var objectType) ||
                !string.Equals(
                    objectType,
                    expected.Name.EndsWith("Mutex", StringComparison.OrdinalIgnoreCase) ? "Mutant" : "Event",
                    StringComparison.OrdinalIgnoreCase) ||
                !TryQueryObjectString(duplicatedHandle, ObjectNameInformation, out var objectName))
            {
                throw new InvalidOperationException($"Could not verify {expected.Name} before closing it.");
            }

            var expectedType = expected.Name.EndsWith("Mutex", StringComparison.OrdinalIgnoreCase)
                ? "Mutant"
                : "Event";
            if (!objectName.EndsWith($"\\{expected.Name}", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(objectType, expectedType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The handle in Roblox did not match {expected.Name}; it was not closed.");
            }
        }
        finally
        {
            CloseHandle(duplicatedHandle);
        }
    }

    private static void VerifyNativeProcessIdentity(
        IntPtr processHandle,
        SingletonProcessIdentity expected)
    {
        if (!GetProcessTimes(processHandle, out var creationTime, out _, out _, out _))
            throw new SingletonProcessGoneException(expected.Pid);

        var creationFileTime = ((long)creationTime.HighDateTime << 32) | creationTime.LowDateTime;
        var creationTicks = DateTime.FromFileTimeUtc(creationFileTime).Ticks;
        if (creationTicks != expected.StartTimeUtcTicks ||
            !QueryProcessImagePath(processHandle, out var imagePath) ||
            !string.Equals(CanonicalizeExecutablePath(imagePath), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new SingletonProcessGoneException(expected.Pid);
        }
    }

    private static bool QueryProcessImagePath(IntPtr processHandle, out string imagePath)
    {
        var buffer = new StringBuilder(32768);
        var length = buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer, ref length))
        {
            imagePath = string.Empty;
            return false;
        }

        imagePath = buffer.ToString();
        return imagePath.Length > 0;
    }

    private static bool TryQueryObjectString(IntPtr handle, int informationClass, out string value)
    {
        var bufferLength = 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                var status = NtQueryObject(handle, informationClass, buffer, bufferLength, out var requiredLength);
                if (status == 0)
                {
                    var unicode = Marshal.PtrToStructure<NativeUnicodeString>(buffer);
                    value = unicode.Buffer == IntPtr.Zero || unicode.Length == 0
                        ? string.Empty
                        : Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2) ?? string.Empty;
                    return value.Length > 0;
                }

                if (status != StatusInfoLengthMismatch || requiredLength <= bufferLength)
                    break;
                bufferLength = requiredLength;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        value = string.Empty;
        return false;
    }

    private static void EnsureProcessIdentity(SingletonProcessIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            process.Refresh();
            if (process.HasExited)
                throw new SingletonProcessGoneException(expected.Pid);

            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                process.StartTime.ToUniversalTime().Ticks != expected.StartTimeUtcTicks ||
                !string.Equals(CanonicalizeExecutablePath(executablePath), expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new SingletonProcessGoneException(expected.Pid);
            }
        }
        catch (SingletonProcessGoneException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            throw new SingletonProcessGoneException(expected.Pid);
        }
    }

    private static bool HasProcessIdentity(SingletonProcessIdentity expected)
    {
        try
        {
            EnsureProcessIdentity(expected);
            return true;
        }
        catch (SingletonProcessGoneException)
        {
            return false;
        }
    }

    private static string CanonicalizeExecutablePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static List<SingletonHandleInfo> ParseSingletonHandles(string output)
    {
        var handles = new List<SingletonHandleInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = HandleLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var nameMatch = SingletonNameRegex().Match(match.Groups["name"].Value.Trim());
            if (nameMatch.Success)
            {
                handles.Add(new SingletonHandleInfo(
                    match.Groups["id"].Value,
                    nameMatch.Value.TrimStart('\\')));
            }
        }

        return handles;
    }

    private static async Task<HandleResult> RunHandleAsync(
        string handlePath,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        // This helper runs in the elevated RAM process. Re-check Authenticode
        // immediately before every launch so a medium-integrity replacement
        // cannot turn the Handle tool path into an elevation boundary.
        ValidateHandleTool(handlePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = handlePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the Sysinternals Handle tool.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup when cancellation races process exit.
            }

            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        return new HandleResult(process.ExitCode, string.Join(Environment.NewLine, new[] { output, error }
            .Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static void EnsureHandleSucceeded(HandleResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Output)
            ? "No diagnostic output was produced."
            : result.Output.Trim();
        throw new InvalidOperationException(
            $"Sysinternals Handle could not {operation} (exit code {result.ExitCode}): {detail}");
    }

    private static async Task<string> EnsureHandleToolAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            ValidateHandleTool(destinationPath);
            return destinationPath;
        }

        using var response = await HttpClient.GetAsync(HandleDownloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archiveBytes = new MemoryStream();
        await download.CopyToAsync(archiveBytes, cancellationToken);
        archiveBytes.Position = 0;

        using var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
        var handleEntry = archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, "handle64.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Microsoft's Handle archive did not contain handle64.exe.");

        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = handleEntry.Open())
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length == 0)
            {
                throw new InvalidOperationException("The downloaded Handle executable was empty.");
            }

            ProtectToolFile(temporaryPath);
            ValidateHandleTool(temporaryPath);
            if (File.Exists(destinationPath))
                throw new InvalidOperationException("A Handle tool appeared while the verified download was being installed.");

            File.Move(temporaryPath, destinationPath);
            ValidateHandleTool(destinationPath);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureProtectedToolDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureNoReparsePathComponents(fullPath);
        var tools = new DirectoryInfo(fullPath);
        var appRoot = tools.Parent
            ?? throw new InvalidOperationException("The Handle tool directory has no protected application root.");

        EnsureProtectedDirectoryExists(appRoot.FullName);
        EnsureProtectedDirectoryExists(tools.FullName);
        VerifyProtectedDirectory(appRoot.FullName);
        VerifyProtectedDirectory(tools.FullName);
    }

    private static void EnsureProtectedDirectoryExists(string path)
    {
        var directory = new DirectoryInfo(path);
        EnsureNoReparsePathComponents(path);
        if (directory.Exists)
        {
            // Never adopt an attacker-created directory by rewriting its ACL.
            // Existing objects must already have RAM's exact owner/permission
            // boundary or startup fails closed.
            VerifyProtectedDirectory(path);
            return;
        }

        var parent = directory.Parent
            ?? throw new InvalidOperationException("The protected tool directory has no existing parent.");
        if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The protected tool directory parent is missing or unsafe.");

        CreateDirectoryWithSecurity(path, CreateProtectedDirectorySecurity());
        EnsureNoReparsePathComponents(path);
        VerifyProtectedDirectory(path);
    }

    private static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(BuiltinAdministratorsSid());
        security.AddAccessRule(CreateToolAccessRule(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        security.AddAccessRule(CreateToolAccessRule(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        security.AddAccessRule(CreateToolAccessRule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        return security;
    }

    private static void CreateDirectoryWithSecurity(string path, DirectorySecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorPointer = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorPointer, descriptor.Length);
            var attributes = new NativeSecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<NativeSecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = false
            };
            if (!CreateDirectoryNative(path, ref attributes))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 183 || !Directory.Exists(path))
                    throw new InvalidOperationException($"Windows could not create the protected Handle tool directory (native error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    private static void ProtectToolFile(string path)
    {
        var file = new FileInfo(path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The downloaded Handle tool is a reparse point and cannot be trusted.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(BuiltinAdministratorsSid());
        security.AddAccessRule(CreateToolAccessRule(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl, InheritanceFlags.None));
        security.AddAccessRule(CreateToolAccessRule(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, InheritanceFlags.None));
        security.AddAccessRule(CreateToolAccessRule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute, InheritanceFlags.None));
        file.SetAccessControl(security);
    }

    private static FileSystemAccessRule CreateToolAccessRule(
        WellKnownSidType sidType,
        FileSystemRights rights,
        InheritanceFlags inheritance)
    {
        var sid = new SecurityIdentifier(sidType, null);
        return new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow);
    }

    private static SecurityIdentifier BuiltinAdministratorsSid() =>
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static SecurityIdentifier BuiltinUsersSid() =>
        new(WellKnownSidType.BuiltinUsersSid, null);

    private static void EnsureNoReparsePathComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The Handle tool path has no filesystem root.");
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"The Handle tool path contains a reparse point: {current}");
            }
        }
    }

    private static void VerifyProtectedDirectory(string path)
    {
        EnsureNoReparsePathComponents(path);
        var security = new DirectoryInfo(path).GetAccessControl();
        VerifyProtectedSecurity(security, path);
    }

    private static void VerifyProtectedFile(string path)
    {
        EnsureNoReparsePathComponents(path);
        var security = new FileInfo(path).GetAccessControl();
        VerifyProtectedSecurity(security, path);
    }

    private static void VerifyProtectedSecurity(CommonObjectSecurity security, string path)
    {
        if (!security.AreAccessRulesProtected)
            throw new InvalidOperationException($"The protected Handle tool path has inheritable permissions: {path}");

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !string.Equals(owner.Value, BuiltinAdministratorsSid().Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The protected Handle tool path has an unexpected owner: {path}");

        var writeRights = FileSystemRights.Write |
                          FileSystemRights.Delete |
                          FileSystemRights.DeleteSubdirectoriesAndFiles |
                          FileSystemRights.ChangePermissions |
                          FileSystemRights.TakeOwnership;
        var administratorsSid = BuiltinAdministratorsSid().Value;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var usersSid = BuiltinUsersSid().Value;
        var allowedSids = new[] { administratorsSid, systemSid, usersSid };
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!allowedSids.Contains(sid, StringComparer.OrdinalIgnoreCase) ||
                (string.Equals(sid, usersSid, StringComparison.OrdinalIgnoreCase) &&
                 (rule.FileSystemRights & writeRights) != 0))
            {
                throw new InvalidOperationException($"The protected Handle tool path has an unexpected access rule: {path}");
            }
        }
    }

    private static void ValidateHandleTool(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var fileName = Path.GetFileName(fullPath);
        if (!string.Equals(fileName, "handle64.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.StartsWith("handle64.exe.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured Handle tool has an unexpected filename.");

        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length == 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The configured Handle tool is missing or not a regular file.");

        var toolDirectory = file.Directory
            ?? throw new InvalidOperationException("The configured Handle tool has no parent directory.");
        var appRoot = toolDirectory.Parent
            ?? throw new InvalidOperationException("The configured Handle tool has no protected application root.");
        VerifyProtectedDirectory(appRoot.FullName);
        VerifyProtectedDirectory(toolDirectory.FullName);
        VerifyProtectedFile(fullPath);

        var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
        using (certificate)
        {
            var signerName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var version = FileVersionInfo.GetVersionInfo(fullPath);
            var isKnownSysinternalsHandle =
                string.Equals(version.CompanyName, "Sysinternals - www.sysinternals.com", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.ProductName, "Sysinternals Handle", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(version.OriginalFilename, "Nthandle.exe", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(version.OriginalFilename, "handle64.exe", StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(signerName, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
                !isKnownSysinternalsHandle ||
                !VerifyAuthenticode(fullPath))
            {
                throw new InvalidOperationException("The configured Handle tool is not the verified Microsoft Sysinternals Handle binary and was not executed.");
            }
        }
    }

    private static bool VerifyAuthenticode(string path)
    {
        var action = WinTrustActionGenericVerifyV2;
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustUiChoiceNone,
                RevocationChecks = WinTrustRevocationChecksNone,
                UnionChoice = WinTrustUnionChoiceFile,
                FileInfo = fileInfoPtr,
                ProvFlags = WinTrustProvFlagSafer
            };
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DuplicateCloseSource = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint WinTrustUiChoiceNone = 2;
    private const uint WinTrustRevocationChecksNone = 0;
    private const uint WinTrustUnionChoiceFile = 1;
    private const uint WinTrustProvFlagSafer = 0;
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProvFlags;
        public uint UIContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateDirectoryW(string path, ref NativeSecurityAttributes securityAttributes);

    private static bool CreateDirectoryNative(string path, ref NativeSecurityAttributes securityAttributes) =>
        CreateDirectoryW(path, ref securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out IntPtr targetHandle,
        uint desiredAccess,
        bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder imageFileName,
        ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        IntPtr handle,
        int informationClass,
        IntPtr information,
        int informationLength,
        out int returnLength);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionIdentifier,
        ref WinTrustData trustData);

    [GeneratedRegex(@"^\s*(?<id>[0-9A-Fa-f]+):\s+\S+\s+(?<name>.+)$")]
    private static partial Regex HandleLineRegex();

    [GeneratedRegex(@"\\ROBLOX_singleton(?:Event|Mutex)$", RegexOptions.IgnoreCase)]
    private static partial Regex SingletonNameRegex();

    private sealed record HandleResult(int ExitCode, string Output);
}

public sealed record UnlockResult(bool Success, int ClosedCount, string[] Messages);

public sealed record SingletonSessionStartResult(
    bool Success,
    SingletonUnlockSession? Session,
    string[] Messages);

public sealed class SingletonUnlockSession : IAsyncDisposable
{
    private readonly string _handlePath;
    private bool _disposed;

    internal SingletonUnlockSession(string handlePath)
    {
        _handlePath = handlePath;
    }

    public Task<UnlockResult> ReleaseAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? settleWindow = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SingletonService.ReleaseHandlesAsync(_handlePath, cancellationToken, settleWindow);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

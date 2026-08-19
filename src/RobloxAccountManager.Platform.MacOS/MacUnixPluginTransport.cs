using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacPluginHello(
    string Token,
    string PluginId,
    int ProcessId,
    DateTimeOffset StartTime,
    string ManifestSha256,
    IReadOnlyList<string> DeclaredCapabilities)
{
    public override string ToString() =>
        $"MacPluginHello {{ PluginId = {PluginId}, ProcessId = {ProcessId}, StartTime = {StartTime:O}, ManifestSha256 = {ManifestSha256}, DeclaredCapabilities = {DeclaredCapabilities.Count}, Token = [REDACTED] }}";
}

public sealed record MacPluginFrame(string Type, JsonElement Payload);

/// <summary>
/// Owner-only Unix-domain transport for macOS plugins. It authenticates both a per-run token and
/// the connecting process's PID/start identity, bounds every frame, and validates peer euid where
/// getpeereid is available. It intentionally has no fallback to TCP or unauthenticated pipes.
/// </summary>
public sealed partial class MacUnixPluginTransport : IAsyncDisposable
{
    private const int MaxFrameBytes = 1024 * 1024;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);
    private readonly string _socketPath;
    private Socket? _listener;
    private ExpectedConnection? _expected;
    private int _failedAuthentications;

    public MacUnixPluginTransport()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobloxAccountManager", "PluginTransport");
        _socketPath = Path.Combine(Path.GetFullPath(root), $"host-{Guid.NewGuid():N}.sock");
    }

    public string SocketPath => _socketPath;

    public string ExpectConnection(
        string pluginId,
        int processId,
        DateTimeOffset processStartTime,
        string manifestSha256,
        IReadOnlyList<string> declaredCapabilities)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || processId <= 0 || processStartTime == default ||
            manifestSha256.Length != 64 || !manifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A complete verified plugin identity is required.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expected = new ExpectedConnection(
            pluginId,
            processId,
            processStartTime,
            manifestSha256.ToLowerInvariant(),
            declaredCapabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            token);
        if (Interlocked.CompareExchange(ref _expected, expected, null) is not null)
            throw new InvalidOperationException("A plugin connection is already pending.");
        Interlocked.Exchange(ref _failedAuthentications, 0);
        return token;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Unix-domain plugin transport is only available on macOS.");
        }

        var parent = Path.GetDirectoryName(_socketPath)!;
        PathSafety.EnsureOwnerOnlyDirectory(parent);
        PathSafety.RejectSymlinkComponents(_socketPath);
        PrepareEndpoint();

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        _listener = listener;
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<Socket> AcceptAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        var listener = _listener ?? throw new InvalidOperationException("The plugin transport is not started.");
        var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!PeerBelongsToCurrentUser(socket))
            {
                throw new UnauthorizedAccessException("The plugin peer is not owned by the current user.");
            }

            var hello = await ReceiveHelloAsync(socket, cancellationToken).ConfigureAwait(false);
            var expected = Volatile.Read(ref _expected)
                ?? throw new UnauthorizedAccessException("No plugin connection is expected.");
            if (!TryGetPeerProcessId(socket, out var peerProcessId)
                || peerProcessId != expected.ProcessId
                || hello.ProcessId != expected.ProcessId
                || hello.StartTime != expected.StartTime
                || !string.Equals(hello.PluginId, expected.PluginId, StringComparison.Ordinal)
                || !string.Equals(hello.ManifestSha256, expected.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !hello.DeclaredCapabilities.Order(StringComparer.Ordinal).SequenceEqual(expected.DeclaredCapabilities, StringComparer.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(hello.Token), Encoding.UTF8.GetBytes(expected.Token))
                || !IsCurrentProcessIdentity(expected.ProcessId, expected.StartTime))
            {
                if (Interlocked.Increment(ref _failedAuthentications) >= 4)
                    Interlocked.CompareExchange(ref _expected, null, expected);
                throw new UnauthorizedAccessException("The plugin peer failed token or process-identity authentication.");
            }

            if (!ReferenceEquals(Interlocked.CompareExchange(ref _expected, null, expected), expected))
                throw new UnauthorizedAccessException("The plugin authentication token was already consumed.");

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task<MacPluginHello> ReceiveHelloAsync(Socket socket, CancellationToken cancellationToken)
    {
        var payload = await ReceiveFrameBytesAsync(socket, cancellationToken).ConfigureAwait(false);
        var hello = JsonSerializer.Deserialize<MacPluginHello>(payload)
            ?? throw new InvalidDataException("Plugin authentication frame was empty.");
        return hello;
    }

    public static async Task<MacPluginFrame> ReceiveFrameAsync(Socket socket, CancellationToken cancellationToken = default)
    {
        var payload = await ReceiveFrameBytesAsync(socket, cancellationToken).ConfigureAwait(false);
        var frame = JsonSerializer.Deserialize<MacPluginFrame>(payload)
            ?? throw new InvalidDataException("Plugin frame was empty.");
        return frame;
    }

    public static Task SendFrameAsync(Socket socket, MacPluginFrame frame, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        return SendFrameBytesAsync(socket, bytes, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        _listener = null;
        if (File.Exists(_socketPath))
        {
            PathSafety.RejectSymlinkComponents(_socketPath);
            File.Delete(_socketPath);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void PrepareEndpoint()
    {
        if (!File.Exists(_socketPath))
        {
            return;
        }

        PathSafety.RejectSymlink(_socketPath);
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(_socketPath));
            throw new IOException("A live plugin transport already owns the socket path.");
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // A refused connection is the only stale endpoint state this class removes. ENOTSOCK
            // and permission errors are rejected rather than unlinking an arbitrary file.
            PathSafety.RejectSymlinkComponents(_socketPath);
            File.Delete(_socketPath);
        }
        catch (SocketException exception)
        {
            throw new IOException("The plugin endpoint is not a removable stale Unix socket.", exception);
        }
    }

    private static async Task<byte[]> ReceiveFrameBytesAsync(Socket socket, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReceiveExactAsync(socket, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBytes));
        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException("Plugin frame exceeded the maximum size.");
        }

        var payload = new byte[length];
        await ReceiveExactAsync(socket, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task SendFrameBytesAsync(Socket socket, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException("Plugin frame exceeded the maximum size.");
        }

        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
        await SendExactAsync(socket, lengthBytes, cancellationToken).ConfigureAwait(false);
        await SendExactAsync(socket, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReceiveExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IoTimeout);
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The plugin peer closed the transport.");
            }

            offset += read;
        }
    }

    private static async Task SendExactAsync(Socket socket, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(IoTimeout);
        var offset = 0;
        while (offset < buffer.Length)
        {
            offset += await socket.SendAsync(buffer[offset..], SocketFlags.None, timeout.Token).ConfigureAwait(false);
        }
    }

    private static bool IsCurrentProcessIdentity(int processId, DateTimeOffset expectedStartTime)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && new DateTimeOffset(process.StartTime.ToUniversalTime()) == expectedStartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool PeerBelongsToCurrentUser(Socket socket)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var result = NativeMethods.GetPeerEid((int)socket.Handle, out var effectiveUid, out _);
        return result == 0 && effectiveUid == NativeMethods.GetEffectiveUid();
    }

    private static bool TryGetPeerProcessId(Socket socket, out int processId)
    {
        processId = 0;
        uint length = sizeof(int);
        return NativeMethods.GetSocketOption(
            (int)socket.Handle,
            NativeMethods.SolLocal,
            NativeMethods.LocalPeerPid,
            out processId,
            ref length) == 0 && processId > 0;
    }

    private static partial class NativeMethods
    {
        internal const int SolLocal = 0;
        internal const int LocalPeerPid = 2;

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getpeereid", SetLastError = true)]
        internal static partial int GetPeerEid(int socket, out uint effectiveUid, out uint effectiveGid);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUid();

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getsockopt", SetLastError = true)]
        internal static partial int GetSocketOption(
            int socket, int level, int optionName, out int optionValue, ref uint optionLength);
    }

    private sealed record ExpectedConnection(
        string PluginId,
        int ProcessId,
        DateTimeOffset StartTime,
        string ManifestSha256,
        string[] DeclaredCapabilities,
        string Token);
}

public sealed record MacPluginProcess(
    Process Process,
    RobloxProcessIdentity Identity,
    int ProcessGroupId);

/// <summary>Supervises only process groups whose PID/start identity was captured at spawn.</summary>
public sealed partial class MacPluginProcessSupervisor
{
    public async Task<MacPluginProcess> StartAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS plugin supervisor is only available on macOS.");
        }

        var processId = SpawnInOwnProcessGroup(executable, arguments);
        var process = Process.GetProcessById(processId);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = await CaptureIdentityAsync(process, cancellationToken).ConfigureAwait(false);
            return new MacPluginProcess(process, identity, process.Id);
        }
        catch
        {
            // The Process instance is the child we just created. Kill and reap it
            // on every setup failure so a setpgid/exec race cannot orphan a plugin.
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            process.Dispose();
            throw;
        }
    }

    private static int SpawnInOwnProcessGroup(string executable, IEnumerable<string> arguments)
    {
        var fullExecutable = Path.GetFullPath(executable);
        if (!File.Exists(fullExecutable)) throw new FileNotFoundException("The plugin entrypoint does not exist.", fullExecutable);
        var strings = new List<IntPtr>();
        IntPtr vector = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        try
        {
            var values = new[] { fullExecutable }.Concat(arguments).ToArray();
            strings.AddRange(values.Select(Marshal.StringToCoTaskMemUTF8));
            vector = Marshal.AllocHGlobal(IntPtr.Size * (strings.Count + 1));
            for (var index = 0; index < strings.Count; index++) Marshal.WriteIntPtr(vector, index * IntPtr.Size, strings[index]);
            Marshal.WriteIntPtr(vector, strings.Count * IntPtr.Size, IntPtr.Zero);

            var result = NativeMethods.SpawnAttributesInit(out attributes);
            if (result != 0) throw new InvalidOperationException($"posix-spawn-attributes-failed:{result}");
            result = NativeMethods.SpawnAttributesSetFlags(ref attributes, NativeMethods.PosixSpawnSetProcessGroup);
            if (result == 0) result = NativeMethods.SpawnAttributesSetProcessGroup(ref attributes, 0);
            if (result != 0) throw new InvalidOperationException($"posix-spawn-process-group-failed:{result}");
            var environment = Marshal.ReadIntPtr(NativeMethods.GetEnvironment());
            result = NativeMethods.Spawn(out var processId, fullExecutable, IntPtr.Zero, ref attributes, vector, environment);
            if (result != 0 || processId <= 0) throw new InvalidOperationException($"posix-spawn-failed:{result}");
            return processId;
        }
        finally
        {
            if (attributes != IntPtr.Zero) _ = NativeMethods.SpawnAttributesDestroy(ref attributes);
            if (vector != IntPtr.Zero) Marshal.FreeHGlobal(vector);
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
        }
    }

    public async Task<bool> TerminateAsync(MacPluginProcess plugin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        cancellationToken.ThrowIfCancellationRequested();
        if (plugin.Process.HasExited || !IsSameProcess(plugin.Process, plugin.Identity))
        {
            return false;
        }

        // Verify the group leader immediately before signalling. A reused PID never receives a
        // signal, and no process outside the verified group is enumerated or killed.
        if (NativeMethods.SendSignalToProcessGroup(-plugin.ProcessGroupId, 15) != 0)
        {
            return false;
        }

        try
        {
            await plugin.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return plugin.Process.HasExited;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<RobloxProcessIdentity> CaptureIdentityAsync(Process process, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var executable = process.MainModule?.FileName ?? string.Empty;
        return new RobloxProcessIdentity(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), executable, string.Empty);
    }

    private static bool IsSameProcess(Process process, RobloxProcessIdentity identity)
    {
        try
        {
            return !process.HasExited
                && process.Id == identity.ProcessId
                && new DateTimeOffset(process.StartTime.ToUniversalTime()) == identity.StartTime
                && string.Equals(process.MainModule?.FileName, identity.ExecutablePath, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static partial class NativeMethods
    {
        internal const short PosixSpawnSetProcessGroup = 0x0002;

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_init")]
        internal static partial int SpawnAttributesInit(out IntPtr attributes);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_destroy")]
        internal static partial int SpawnAttributesDestroy(ref IntPtr attributes);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_setflags")]
        internal static partial int SpawnAttributesSetFlags(ref IntPtr attributes, short flags);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawnattr_setpgroup")]
        internal static partial int SpawnAttributesSetProcessGroup(ref IntPtr attributes, int processGroup);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Spawn(
            out int processId,
            string path,
            IntPtr fileActions,
            ref IntPtr attributes,
            IntPtr arguments,
            IntPtr environment);

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_NSGetEnviron")]
        internal static partial IntPtr GetEnvironment();

        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
        internal static partial int SendSignalToProcessGroup(int processGroupId, int signal);
    }
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RobloxAltClient.Plugins;

public sealed class PluginHostService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ExpectedConnection> _expected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginConnection> _connections = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _unauthenticatedLimit = new(8, 8);
    private readonly ConcurrentDictionary<Guid, Task> _connectionTasks = new();
    private readonly Task _acceptLoop;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string PipeName => PluginProtocol.PipePrefix + SessionId;
    public event EventHandler<PluginConnection>? Connected;
    public event EventHandler<PluginConnection>? Disconnected;
    public event EventHandler<(PluginConnection Connection, PluginEnvelope Envelope)>? MessageReceived;
    public PriorityInputLeaseCoordinator InputLeases { get; } = new();
    public Func<string, IReadOnlyList<PluginInputEvent>, CancellationToken, Task<BackgroundInputResult>>? InputDispatcher { get; set; }

    public PluginHostService()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string CreateLaunchToken(string pluginId, string manifestHash, IReadOnlyCollection<string> capabilities)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _expected[token] = new ExpectedConnection(pluginId, manifestHash,
            new HashSet<string>(capabilities, StringComparer.Ordinal), null);
        return token;
    }

    public void BindLaunchProcess(string token, int processId)
    {
        if (processId <= 0 || !_expected.TryGetValue(token, out var expected))
            throw new InvalidOperationException("The plugin launch token is unknown or the process id is invalid.");
        _expected[token] = expected with { ProcessId = processId };
    }

    public void RevokeLaunchToken(string token) => _expected.TryRemove(token, out _);

    public IReadOnlyList<string> ConnectedPluginIds => _connections.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public async Task SendAsync(string pluginId, string type, object payload, string requestId = "", CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(pluginId, out var connection))
            throw new InvalidOperationException($"Plugin '{pluginId}' is not connected.");
        await connection.SendAsync(type, payload, requestId, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                if (!await _unauthenticatedLimit.WaitAsync(TimeSpan.Zero, _shutdown.Token).ConfigureAwait(false))
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = null;
                    continue;
                }
                var connectedPipe = pipe!;
                var taskId = Guid.NewGuid();
                var connectionTask = Task.Run(async () =>
                {
                    try { await HandleConnectionAsync(connectedPipe).ConfigureAwait(false); }
                    finally { _unauthenticatedLimit.Release(); }
                }, _shutdown.Token);
                _connectionTasks[taskId] = connectionTask;
                _ = connectionTask.ContinueWith(completed => _connectionTasks.TryRemove(taskId, out var ignored), TaskScheduler.Default);
                pipe = null;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                if (!_shutdown.IsCancellationRequested) await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        PluginConnection? connection = null;
        var registered = false;
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var handshakeEnvelope = await PluginWire.ReadAsync(pipe, handshakeTimeout.Token).ConfigureAwait(false);
            if (handshakeEnvelope is null || !string.Equals(handshakeEnvelope.Type, "plugin.hello", StringComparison.Ordinal))
                throw new InvalidDataException("The first plugin message must be plugin.hello.");
            var handshake = handshakeEnvelope.Payload.Deserialize<PluginHandshake>(PluginJson.Options)
                            ?? throw new InvalidDataException("Plugin handshake payload is invalid.");
            if (!_expected.TryGetValue(handshake.Token, out var expected) ||
                !string.Equals(expected.PluginId, handshake.PluginId, StringComparison.Ordinal) ||
                handshake.ProtocolMajor != PluginProtocol.CurrentMajor ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected.ManifestHash),
                    Encoding.UTF8.GetBytes(handshake.ManifestSha256)) ||
                !handshake.DeclaredCapabilities.ToHashSet(StringComparer.Ordinal).SetEquals(expected.GrantedCapabilities) ||
                !TryGetClientProcessId(pipe, out var clientProcessId) ||
                expected.ProcessId is null || clientProcessId != expected.ProcessId ||
                handshake.ProcessId != clientProcessId ||
                !_expected.TryRemove(handshake.Token, out _))
            {
                await PluginWire.WriteAsync(pipe, new PluginEnvelope("host.reject", Guid.NewGuid().ToString("N"),
                    JsonSerializer.SerializeToElement(new { reason = "authentication-failed" }, PluginJson.Options)), _shutdown.Token);
                return;
            }

            connection = new PluginConnection(handshake.PluginId, pipe, handshake.DeclaredCapabilities,
                expected.GrantedCapabilities);
            if (!_connections.TryAdd(handshake.PluginId, connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                connection = null;
                throw new InvalidOperationException("A plugin with this id is already connected.");
            }
            registered = true;
            await connection.SendAsync("host.accept", new { protocolMajor = PluginProtocol.CurrentMajor, protocolMinor = PluginProtocol.CurrentMinor, pipe = PipeName }, handshakeEnvelope.RequestId, _shutdown.Token);
            Connected?.Invoke(this, connection);
            while (!_shutdown.IsCancellationRequested)
            {
                var envelope = await PluginWire.ReadAsync(pipe, _shutdown.Token).ConfigureAwait(false);
                if (envelope is null) break;
                if (!connection.IsAuthorized(envelope.Type))
                {
                    await connection.SendAsync("host.reject", new { reason = "capability-denied", messageType = envelope.Type },
                        envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(envelope.Type, "input.post", StringComparison.Ordinal))
                {
                    await DispatchInputAsync(connection, envelope).ConfigureAwait(false);
                    continue;
                }
                MessageReceived?.Invoke(this, (connection, envelope));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch
        {
            // The process supervisor surfaces a useful diagnostic for a failed plugin;
            // this transport loop intentionally remains fail-closed.
        }
        finally
        {
            if (connection is not null && registered)
            {
                _connections.TryRemove(connection.PluginId, out _);
                Disconnected?.Invoke(this, connection);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchInputAsync(PluginConnection connection, PluginEnvelope envelope)
    {
        try
        {
            var request = envelope.Payload.Deserialize<InputPostRequest>(PluginJson.Options)
                          ?? throw new InvalidDataException("input.post payload is invalid.");
            var lease = await InputLeases.TryAcquireAsync(request.AccountId, connection.PluginId,
                PriorityForPlugin(connection.PluginId), TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);
            if (lease is null)
            {
                await connection.SendAsync("input.result", BackgroundInputResult.Failure("busy", "The account input lease is busy.", nint.Zero, nint.Zero), envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                return;
            }
            await using (lease)
            {
                var result = InputDispatcher is null
                    ? BackgroundInputResult.Failure("unavailable", "The host input broker is unavailable.", nint.Zero, nint.Zero)
                    : await InputDispatcher(request.AccountId, request.Events, _shutdown.Token).ConfigureAwait(false);
                await connection.SendAsync("input.result", result, envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            await connection.SendAsync("input.result", BackgroundInputResult.Failure("invalid-request", ex.Message, nint.Zero, nint.Zero), envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
        }
    }

    private static int PriorityForPlugin(string pluginId) => pluginId switch
    {
        "io.github.codysimonds65.ram.macros" => 300,
        "io.github.codysimonds65.ram.ocr" => 200,
        "io.github.codysimonds65.ram.afk" => 100,
        _ => 50
    };

    private sealed record InputPostRequest(string AccountId, IReadOnlyList<PluginInputEvent> Events);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(_connectionTasks.Values).ConfigureAwait(false); } catch { }
        foreach (var connection in _connections.Values) await connection.DisposeAsync().ConfigureAwait(false);
        _unauthenticatedLimit.Dispose();
        _shutdown.Dispose();
    }

    private sealed record ExpectedConnection(string PluginId, string ManifestHash, IReadOnlySet<string> GrantedCapabilities, int? ProcessId);

    private static bool TryGetClientProcessId(NamedPipeServerStream pipe, out int processId)
    {
        processId = 0;
        return GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out int clientProcessId);
}

public sealed class PluginConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    internal PluginConnection(string pluginId, Stream stream, IReadOnlyList<string> declaredCapabilities, IReadOnlySet<string> grantedCapabilities)
    {
        PluginId = pluginId;
        _stream = stream;
        DeclaredCapabilities = declaredCapabilities;
        GrantedCapabilities = grantedCapabilities;
    }

    public string PluginId { get; }
    public IReadOnlyList<string> DeclaredCapabilities { get; }
    public IReadOnlySet<string> GrantedCapabilities { get; }

    internal bool IsAuthorized(string type)
    {
        var required = type switch
        {
            "accounts.list" or "account.snapshot" => PluginCapabilities.HostAccountsRead,
            "account.events.subscribe" => PluginCapabilities.HostAccountEvents,
            "activity.list" => PluginCapabilities.HostActivityRead,
            "theme.get" or "theme.subscribe" => PluginCapabilities.HostThemeRead,
            "input.post" or "input.lease.acquire" => PluginCapabilities.HostInputBackground,
            "action.register" => PluginCapabilities.HostActionsRegister,
            "action.invoke" => PluginCapabilities.HostActionsInvoke,
            "screen.capture" => PluginCapabilities.SystemReadScreen,
            "global-input.subscribe" => PluginCapabilities.SystemWatchGlobalInput,
            "action.result" or "action.progress" or "plugin.heartbeat" or "plugin.shutdown" => "",
            _ => null
        };
        return required is not null && (required.Length == 0 || GrantedCapabilities.Contains(required));
    }

    public async Task SendAsync(string type, object payload, string requestId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PluginConnection));
        var envelope = new PluginEnvelope(type, requestId, JsonSerializer.SerializeToElement(payload, PluginJson.Options));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await PluginWire.WriteAsync(_stream, envelope, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        finally { _writeGate.Release(); _writeGate.Dispose(); }
    }
}

internal static class PluginWire
{
    public static async Task WriteAsync(Stream stream, PluginEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, PluginJson.Options);
        if (bytes.Length > PluginProtocol.MaxMessageBytes) throw new InvalidDataException("Plugin message exceeds the size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PluginEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > PluginProtocol.MaxMessageBytes) throw new InvalidDataException("Plugin message length is invalid.");
        var bytes = new byte[length];
        if (!await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false)) return null;
        return JsonSerializer.Deserialize<PluginEnvelope>(bytes, PluginJson.Options)
               ?? throw new InvalidDataException("Plugin message is invalid JSON.");
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}

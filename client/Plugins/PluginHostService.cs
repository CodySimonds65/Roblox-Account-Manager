using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace RobloxAltClient.Plugins;

public sealed class PluginHostService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ExpectedConnection> _expected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<int>> _hotkeySubscriptions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _unauthenticatedLimit = new(8, 8);
    private readonly ConcurrentDictionary<Guid, Task> _connectionTasks = new();
    private readonly ConcurrentDictionary<string, Task> _inputTasks = new(StringComparer.Ordinal);
    private readonly object _inputDispatchGate = new();
    private readonly Dictionary<string, ActiveInputDispatch> _activeInputDispatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _completedInputRequests = new(StringComparer.Ordinal);
    private readonly Task _acceptLoop;
    private readonly Task _heartbeatLoop;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string PipeName => PluginProtocol.PipePrefix + SessionId;
    public event EventHandler<PluginConnection>? Connected;
    public event EventHandler<PluginConnection>? Disconnected;
    public event EventHandler<(PluginConnection Connection, PluginEnvelope Envelope)>? MessageReceived;
    public PriorityInputLeaseCoordinator InputLeases { get; } = new();
    public Func<string, IReadOnlyList<PluginInputEvent>, CancellationToken, Task<BackgroundInputResult>>? InputDispatcher { get; set; }
    public Func<string, IReadOnlyList<PluginInputEvent>, InputDeliveryIntent, string?, CancellationToken, Task<BackgroundInputResult>>? InputDispatcherWithIntent { get; set; }
    public Func<string, string, string?, IReadOnlyList<PluginInputEvent>, InputDeliveryIntent, string?, CancellationToken, Task<BackgroundInputResult>>? InputDispatcherWithSession { get; set; }

    private const int MaxActiveDispatchesPerPlugin = 8;
    private const int MaxActiveDispatchesGlobal = 32;
    private const int MaxActiveEventsPerPlugin = 20_000;
    private static readonly TimeSpan CompletedRequestRetention = TimeSpan.FromMinutes(2);

    /// <summary>Cancel detached input work before a managed account is terminated.</summary>
    public void CancelInputDispatchesForAccount(string accountId)
    {
        ActiveInputDispatch[] dispatches;
        lock (_inputDispatchGate)
            dispatches = _activeInputDispatches.Values.Where(dispatch =>
                string.Equals(dispatch.AccountId, accountId, StringComparison.Ordinal)).ToArray();
        foreach (var dispatch in dispatches) TryCancel(dispatch.Cancellation);
    }

    /// <summary>Cancel detached input work before a plugin process is stopped.</summary>
    public void CancelInputDispatchesForPlugin(string pluginId)
    {
        ActiveInputDispatch[] dispatches;
        lock (_inputDispatchGate)
            dispatches = _activeInputDispatches.Values.Where(dispatch =>
                string.Equals(dispatch.PluginId, pluginId, StringComparison.Ordinal)).ToArray();
        foreach (var dispatch in dispatches) TryCancel(dispatch.Cancellation);
    }

    public PluginHostService()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _heartbeatLoop = Task.Run(HeartbeatLoopAsync);
    }

    internal static RawSecurityDescriptor CreatePipeSecurityDescriptor() => new(PipeSecuritySddl);

    private NamedPipeServerStream CreateHostPipe()
    {
        var descriptor = CreatePipeSecurityDescriptor();
        var binaryForm = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binaryForm, 0);
        var descriptorPointer = Marshal.AllocHGlobal(binaryForm.Length);
        try
        {
            Marshal.Copy(binaryForm, 0, descriptorPointer, binaryForm.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorPointer,
                InheritHandle = 0
            };
            var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
            try
            {
                Marshal.StructureToPtr(attributes, attributesPointer, false);
                var handle = CreateNamedPipeW(@"\\.\pipe\" + PipeName,
                    PipeAccessDuplex | FileFlagOverlapped,
                    PipeTypeByte | PipeReadmodeByte | PipeWait,
                    8, 4096, 4096, 0, attributesPointer);
                if (handle == nint.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The plugin host pipe could not be created.");
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false,
                    new SafePipeHandle(handle, ownsHandle: true));
            }
            finally
            {
                Marshal.FreeHGlobal(attributesPointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorPointer);
        }
    }

    internal const string PipeSecuritySddl = "D:(A;;GRGW;;;WD)S:(ML;;NWNX;;;ME)";
    private const uint PipeAccessDuplex = 0x3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x0;
    private const uint PipeReadmodeByte = 0x0;
    private const uint PipeWait = 0x0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateNamedPipeW(string pipeName, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeOut, nint securityAttributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    public string CreateLaunchToken(string pluginId, string manifestHash, IReadOnlyCollection<string> capabilities)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _expected[token] = new ExpectedConnection(pluginId, manifestHash,
            new HashSet<string>(capabilities, StringComparer.Ordinal), null, null, DateTime.UtcNow);
        _ = Task.Delay(TimeSpan.FromSeconds(30), _shutdown.Token).ContinueWith(_ => RevokeLaunchToken(token), TaskScheduler.Default);
        return token;
    }

    public void BindLaunchProcess(string token, int processId, long processStartTimeUtcTicks)
    {
        if (processId <= 0 || processStartTimeUtcTicks <= 0 || !_expected.TryGetValue(token, out var expected))
            throw new InvalidOperationException("The plugin launch token is unknown or the process id is invalid.");
        _expected[token] = expected with { ProcessId = processId, ProcessStartTimeUtcTicks = processStartTimeUtcTicks };
    }

    public void RevokeLaunchToken(string token) => _expected.TryRemove(token, out _);

    public IReadOnlyList<string> ConnectedPluginIds => _connections.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public void BroadcastHotkey(string type, int virtualKey)
    {
        if (_hotkeySubscriptions.IsEmpty) return;
        foreach (var (pluginId, keys) in _hotkeySubscriptions)
        {
            if (!keys.Contains(virtualKey) || !_connections.TryGetValue(pluginId, out var connection)) continue;
            try
            {
                _ = connection.SendAsync(type, new { virtualKey }, "", _shutdown.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _hotkeySubscriptions.TryRemove(pluginId, out _);
            }
        }
    }

    internal void SetHotkeySubscription(string pluginId, IReadOnlyCollection<int> virtualKeys)
    {
        if (virtualKeys.Count == 0) { _hotkeySubscriptions.TryRemove(pluginId, out _); return; }
        _hotkeySubscriptions[pluginId] = new HashSet<int>(virtualKeys);
    }

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
                pipe = CreateHostPipe();
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
            if (!TryGetClientProcessId(pipe, out var connectingProcessId) ||
                !_expected.Values.Any(expected => expected.ProcessId == connectingProcessId))
            {
                return;
            }
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
                handshake.ProtocolMinor > PluginProtocol.CurrentMinor ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected.ManifestHash),
                    Encoding.UTF8.GetBytes(handshake.ManifestSha256)) ||
                !handshake.DeclaredCapabilities.ToHashSet(StringComparer.Ordinal).SetEquals(expected.GrantedCapabilities) ||
                !TryGetClientProcessId(pipe, out var clientProcessId) ||
                expected.ProcessId is null || clientProcessId != expected.ProcessId ||
                handshake.ProcessId != clientProcessId ||
                expected.ProcessStartTimeUtcTicks is null || handshake.ProcessStartTimeUtcTicks != expected.ProcessStartTimeUtcTicks ||
                !TryGetProcessStartTicks(clientProcessId, out var clientStartTicks) || clientStartTicks != expected.ProcessStartTimeUtcTicks ||
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
                if (envelope.ProtocolMajor != PluginProtocol.CurrentMajor || envelope.ProtocolMinor > PluginProtocol.CurrentMinor)
                {
                    await connection.SendAsync("host.reject", new { reason = "protocol-version-unsupported" }, envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                    break;
                }
                if (!connection.IsAuthorized(envelope.Type))
                {
                    await connection.SendAsync("host.reject", new { reason = "capability-denied", messageType = envelope.Type },
                        envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(envelope.Type, "plugin.heartbeat", StringComparison.Ordinal))
                {
                    connection.TouchHeartbeat();
                    continue;
                }
                if (string.Equals(envelope.Type, "input.post", StringComparison.Ordinal))
                {
                    await DispatchInputAsync(connection, envelope).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(envelope.Type, "hotkey.subscribe", StringComparison.Ordinal))
                {
                    HandleHotkeySubscribe(connection, envelope);
                    continue;
                }
                if (envelope.Type is "input.session.open" or "input.session.activate" or "input.session.close")
                {
                    MessageReceived?.Invoke(this, (connection, envelope));
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
            if (connection is not null)
                CancelInputDispatchesForPlugin(connection.PluginId);
            if (connection is not null && registered)
            {
                _connections.TryRemove(connection.PluginId, out _);
                _hotkeySubscriptions.TryRemove(connection.PluginId, out _);
                Disconnected?.Invoke(this, connection);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                foreach (var connection in _connections.Values)
                {
                    if (connection.HeartbeatSeen && DateTime.UtcNow - connection.LastHeartbeatUtc > TimeSpan.FromSeconds(45))
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task DispatchInputAsync(PluginConnection connection, PluginEnvelope envelope)
    {
        ActiveInputDispatch? dispatch = null;
        try
        {
            var request = envelope.Payload.Deserialize<InputPostRequest>(PluginJson.Options)
                          ?? throw new InvalidDataException("input.post payload is invalid.");
            ValidateInputRequest(request);
            var intent = ParseDeliveryIntent(request.DeliveryIntent);
            var traceId = string.IsNullOrWhiteSpace(request.TraceId) ? envelope.RequestId : request.TraceId;
            if (!TryReserveInputDispatch(connection.PluginId, envelope.RequestId, request, out dispatch, out var reservationError))
            {
                await connection.SendAsync("input.result", BackgroundInputResult.Failure(reservationError.Code,
                    reservationError.Message, nint.Zero, nint.Zero), envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                return;
            }
            if (!connection.GrantedCapabilities.Contains(PluginCapabilities.HostInputForegroundReal))
            {
                CompleteInputDispatch(dispatch);
                await connection.SendAsync("input.result", BackgroundInputResult.Failure(
                    "foreground-required",
                    "This input capability is message-only and Roblox foreground automation requires explicit foreground-real consent.",
                    nint.Zero, nint.Zero) with
                {
                    RequestedCount = request.Events.Count,
                    TraceId = traceId
                }, envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                return;
            }
            var activeDispatch = dispatch!;
            using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, activeDispatch.Cancellation.Token);
            IAsyncDisposable? lease;
            try
            {
                lease = await InputLeases.TryAcquireAsync(request.AccountId, connection.PluginId,
                    PriorityForPlugin(connection.PluginId), TimeSpan.FromSeconds(2), leaseCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                CompleteInputDispatch(activeDispatch);
                if (!connection.IsDisposed)
                {
                    try
                    {
                        await connection.SendAsync("input.result",
                            BackgroundInputResult.Failure("canceled", "Input dispatch was canceled before acquiring the account lease.", nint.Zero, nint.Zero),
                            envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
                return;
            }
            if (lease is null)
            {
                CompleteInputDispatch(activeDispatch);
                await connection.SendAsync("input.result", BackgroundInputResult.Failure("busy", "The account input lease is busy.", nint.Zero, nint.Zero), envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
                return;
            }
            // The paced dispatch runs detached from the read loop so heartbeats and
            // other messages keep flowing during a long macro. The lease is held
            // across the whole paced run, and pacing is cancelled if the plugin
            // disconnects or the host shuts down.
            var dispatchKey = activeDispatch.Key;
            var run = RunPacedDispatchAsync(connection, envelope.RequestId, request, intent, traceId, lease, activeDispatch);
            _inputTasks[dispatchKey] = run;
            _ = run.ContinueWith(completed => _inputTasks.TryRemove(dispatchKey, out var removed), TaskScheduler.Default);
            dispatch = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            await connection.SendAsync("input.result", BackgroundInputResult.Failure("invalid-request", ex.Message, nint.Zero, nint.Zero), envelope.RequestId, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Host shutdown cancels the transport and all outstanding dispatches.
        }
        finally
        {
            // A reservation handed to the detached run is completed by that run;
            // validation/lease failures must release it on this path.
            if (dispatch is not null) CompleteInputDispatch(dispatch);
        }
    }

    private async Task RunPacedDispatchAsync(PluginConnection connection, string requestId, InputPostRequest request,
        InputDeliveryIntent intent, string? traceId, IAsyncDisposable lease, ActiveInputDispatch dispatch)
    {
        await using (lease)
        {
            try
            {
                using var pacingSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, dispatch.Cancellation.Token);
                var monitor = Task.Run(async () =>
                {
                    try
                    {
                        while (!connection.IsDisposed && !pacingSource.IsCancellationRequested)
                            await Task.Delay(250, pacingSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    pacingSource.Cancel();
                });
                try
                {
                    var result = InputDispatcherWithSession is not null
                        ? await InputDispatcherWithSession(connection.PluginId, request.AccountId, request.SessionId, request.Events, intent, traceId, pacingSource.Token).ConfigureAwait(false)
                        : InputDispatcherWithIntent is not null
                            ? await InputDispatcherWithIntent(request.AccountId, request.Events, intent, traceId, pacingSource.Token).ConfigureAwait(false)
                        : InputDispatcher is null
                            ? BackgroundInputResult.Failure("unavailable", "The host input broker is unavailable.", nint.Zero, nint.Zero)
                            : await InputDispatcher(request.AccountId, request.Events, pacingSource.Token).ConfigureAwait(false);
                    await connection.SendAsync("input.result", result, requestId, _shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    pacingSource.Cancel();
                    try { await monitor.ConfigureAwait(false); } catch { }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The plugin disconnected or the host is shutting down. A live
                // connection still receives an explicit cancellation result.
                if (!connection.IsDisposed && !_shutdown.IsCancellationRequested && !dispatch.Cancellation.IsCancellationRequested)
                {
                    try
                    {
                        await connection.SendAsync("input.result",
                            BackgroundInputResult.Failure("canceled", "Input dispatch was canceled.", nint.Zero, nint.Zero),
                            requestId, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                if (!connection.IsDisposed && !_shutdown.IsCancellationRequested)
                {
                    try
                    {
                        await connection.SendAsync("input.result",
                            BackgroundInputResult.Failure("dispatch-error", ex.Message, nint.Zero, nint.Zero),
                            requestId, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            finally { CompleteInputDispatch(dispatch); }
        }
    }

    private void HandleHotkeySubscribe(PluginConnection connection, PluginEnvelope envelope)
    {
        try
        {
            if (envelope.Payload.TryGetProperty("virtualKeys", out var keysElement) &&
                keysElement.ValueKind == JsonValueKind.Array &&
                keysElement.GetArrayLength() is >= 1 and <= 32)
            {
                var keys = new List<int>(keysElement.GetArrayLength());
                foreach (var keyElement in keysElement.EnumerateArray())
                {
                    if (!keyElement.TryGetInt32(out var vk) || vk is < 1 or > 255)
                        throw new InvalidDataException("hotkey.subscribe virtual keys are invalid.");
                    keys.Add(vk);
                }
                SetHotkeySubscription(connection.PluginId, keys);
                return;
            }
            throw new InvalidDataException("hotkey.subscribe payload is invalid.");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            _ = connection.SendAsync("host.reject", new { reason = "invalid-request", messageType = "hotkey.subscribe", detail = ex.Message },
                envelope.RequestId, _shutdown.Token);
        }
    }

    private bool TryReserveInputDispatch(string pluginId, string requestId, InputPostRequest request,
        out ActiveInputDispatch? dispatch, out (string Code, string Message) error)
    {
        dispatch = null;
        error = default;
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200)
        {
            error = ("invalid-request", "input.post request id is invalid.");
            return false;
        }

        var key = pluginId + "\u001f" + requestId;
        var now = DateTime.UtcNow;
        lock (_inputDispatchGate)
        {
            foreach (var completed in _completedInputRequests.Where(pair => now - pair.Value >= CompletedRequestRetention).Select(pair => pair.Key).ToArray())
                _completedInputRequests.Remove(completed);

            if (_activeInputDispatches.ContainsKey(key) || _completedInputRequests.ContainsKey(key))
            {
                error = ("duplicate-request", "This input request id was already dispatched.");
                return false;
            }

            var pluginDispatches = _activeInputDispatches.Values.Where(active =>
                string.Equals(active.PluginId, pluginId, StringComparison.Ordinal)).ToArray();
            var activeEvents = pluginDispatches.Sum(active => active.EventCount);
            if (_activeInputDispatches.Count >= MaxActiveDispatchesGlobal ||
                pluginDispatches.Length >= MaxActiveDispatchesPerPlugin ||
                activeEvents > MaxActiveEventsPerPlugin - request.Events.Count)
            {
                error = ("quota", "The plugin input dispatch quota is exhausted.");
                return false;
            }

            dispatch = new ActiveInputDispatch(key, pluginId, request.AccountId, request.Events.Count,
                new CancellationTokenSource());
            _activeInputDispatches[key] = dispatch;
            return true;
        }
    }

    private void CompleteInputDispatch(ActiveInputDispatch? dispatch)
    {
        if (dispatch is null) return;
        lock (_inputDispatchGate)
        {
            if (!_activeInputDispatches.Remove(dispatch.Key)) return;
            _completedInputRequests[dispatch.Key] = DateTime.UtcNow;
        }
        dispatch.Cancellation.Dispose();
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static InputDeliveryIntent ParseDeliveryIntent(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" or "auto" or "foreground" or "foreground-real" => InputDeliveryIntent.Default,
        "postmessageprobe" or "post-message-probe" or "post-message" or "message-only" or "background-message" => InputDeliveryIntent.PostMessageProbe,
        _ => throw new InvalidDataException("input.post deliveryIntent is unsupported.")
    };

    private static void ValidateInputRequest(InputPostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AccountId) || request.AccountId.Length > 200)
            throw new InvalidDataException("input.post account id is invalid.");
        if (request.Events is null || request.Events.Count == 0 || request.Events.Count > 10_000)
            throw new InvalidDataException("input.post event count is outside the permitted range.");
        if (request.TraceId is not null && request.TraceId.Length > 200)
            throw new InvalidDataException("input.post traceId is invalid.");
        long previousOffset = -1;
        var burstStart = -1L;
        var burstCount = 0;
        foreach (var input in request.Events)
        {
            if (!double.IsFinite(input.NormalizedX) || !double.IsFinite(input.NormalizedY) ||
                input.NormalizedX < 0 || input.NormalizedX > 1 || input.NormalizedY < 0 || input.NormalizedY > 1)
                throw new InvalidDataException("input.post coordinates must be finite normalized values.");
            if (input.OffsetMicroseconds < 0 || input.OffsetMicroseconds > TimeSpan.FromHours(1).Ticks / 10)
                throw new InvalidDataException("input.post timing is outside the permitted range.");
            if (input.OffsetMicroseconds < previousOffset)
                throw new InvalidDataException("input.post events must be ordered by offset.");
            if (burstStart < 0 || input.OffsetMicroseconds - burstStart > 1_000_000)
            {
                burstStart = input.OffsetMicroseconds;
                burstCount = 0;
            }
            if (++burstCount > 2_000)
                throw new InvalidDataException("input.post event rate exceeds the permitted burst limit.");
            previousOffset = input.OffsetMicroseconds;
            if (input.Kind is PluginInputKind.KeyDown or PluginInputKind.KeyUp)
            {
                if (input.VirtualKey is < 1 or > 255 || input.ScanCode is < 0 or > 255)
                    throw new InvalidDataException("input.post key metadata is invalid.");
            }
            else if (input.Kind is PluginInputKind.MouseButtonDown or PluginInputKind.MouseButtonUp)
            {
                // RAM's stable wire encoding is 0=left, 1=right, 2=middle.
                if (input.Button is < 0 or > 2)
                    throw new InvalidDataException("input.post mouse button is invalid.");
            }
            else if (input.Kind == PluginInputKind.MouseWheel && Math.Abs(input.WheelDelta) > 120_000)
                throw new InvalidDataException("input.post wheel delta is invalid.");
        }
    }

    private static int PriorityForPlugin(string pluginId) => pluginId switch
    {
        "io.github.codysimonds65.ram.macros" => 300,
        "io.github.codysimonds65.ram.ocr" => 200,
        "io.github.codysimonds65.ram.afk" => 100,
        _ => 50
    };

    private sealed record InputPostRequest(string AccountId, IReadOnlyList<PluginInputEvent> Events,
        string? DeliveryIntent = null, string? TraceId = null, string? SessionId = null);

    private sealed record ActiveInputDispatch(string Key, string PluginId, string AccountId, int EventCount,
        CancellationTokenSource Cancellation);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        ActiveInputDispatch[] dispatches;
        lock (_inputDispatchGate) dispatches = _activeInputDispatches.Values.ToArray();
        foreach (var dispatch in dispatches) dispatch.Cancellation.Cancel();
        try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await _heartbeatLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(_connectionTasks.Values).ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(_inputTasks.Values.ToArray()).ConfigureAwait(false); } catch { }
        foreach (var connection in _connections.Values) await connection.DisposeAsync().ConfigureAwait(false);
        _unauthenticatedLimit.Dispose();
        _shutdown.Dispose();
    }

    private sealed record ExpectedConnection(string PluginId, string ManifestHash, IReadOnlySet<string> GrantedCapabilities,
        int? ProcessId, long? ProcessStartTimeUtcTicks, DateTime CreatedUtc);

    private static bool TryGetProcessStartTicks(int processId, out long ticks)
    {
        ticks = 0;
        try { using var process = Process.GetProcessById(processId); process.Refresh(); ticks = process.StartTime.ToUniversalTime().Ticks; return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

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
    private long _lastHeartbeatUtcTicks = DateTime.UtcNow.Ticks;

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
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal bool HeartbeatSeen { get; private set; }
    internal DateTime LastHeartbeatUtc => new(Interlocked.Read(ref _lastHeartbeatUtcTicks), DateTimeKind.Utc);

    internal void TouchHeartbeat()
    {
        HeartbeatSeen = true;
        Interlocked.Exchange(ref _lastHeartbeatUtcTicks, DateTime.UtcNow.Ticks);
    }

    internal bool IsAuthorized(string type)
    {
        var required = type switch
        {
            "accounts.list" or "account.snapshot" => PluginCapabilities.HostAccountsRead,
            "account.events.subscribe" => PluginCapabilities.HostAccountEvents,
            "activity.list" => PluginCapabilities.HostActivityRead,
            "theme.get" or "theme.subscribe" => PluginCapabilities.HostThemeRead,
            "input.post" or "input.lease.acquire" =>
                (GrantedCapabilities.Contains(PluginCapabilities.HostInputBackground) ||
                 GrantedCapabilities.Contains(PluginCapabilities.HostInputBackgroundMessages) ||
                 GrantedCapabilities.Contains(PluginCapabilities.HostInputForegroundReal))
                    ? "__input-authorized__" : null,
            "input.session.open" or "input.session.activate" or "input.session.close" =>
                GrantedCapabilities.Contains(PluginCapabilities.HostInputForegroundReal) ? "__foreground-input__" : null,
            "action.register" => PluginCapabilities.HostActionsRegister,
            "action.invoke" => PluginCapabilities.HostActionsInvoke,
            "screen.capture" => PluginCapabilities.SystemReadScreen,
            "global-input.subscribe" => PluginCapabilities.SystemWatchGlobalInput,
            "action.result" or "action.progress" or "plugin.heartbeat" or "plugin.shutdown" or "diagnostic.log" or "hotkey.subscribe" => "",
            _ => null
        };
        return required is not null && (required.Length == 0 || required == "__input-authorized__" || required == "__foreground-input__" || GrantedCapabilities.Contains(required));
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

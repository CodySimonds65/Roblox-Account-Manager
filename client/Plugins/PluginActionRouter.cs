using System.Collections.Concurrent;
using System.Text.Json;

namespace RobloxAltClient.Plugins;

public sealed record ActionInvocationResult(bool Accepted, string Code, string Message, JsonElement? Data = null)
{
    public static ActionInvocationResult Fail(string code, string message) => new(false, code, message);
}

public sealed class PluginActionRouter : IAsyncDisposable
{
    private readonly PluginHostService _host;
    private readonly ConcurrentDictionary<string, RegisteredAction> _actions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ActionResult>> _pending = new(StringComparer.Ordinal);

    public PluginActionRouter(PluginHostService host)
    {
        _host = host;
        _host.MessageReceived += Host_MessageReceived;
        _host.Disconnected += Host_Disconnected;
    }

    public IReadOnlyList<ActionDescriptor> Actions => _actions.Values.Select(value => value.Descriptor).OrderBy(value => value.ActionId, StringComparer.Ordinal).ToArray();

    public async Task<ActionInvocationResult> InvokeAsync(ActionInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!_actions.TryGetValue(invocation.ActionId, out var action))
            return ActionInvocationResult.Fail("missing-action", $"Action '{invocation.ActionId}' is not registered.");
        var requestId = string.IsNullOrWhiteSpace(invocation.RequestId) ? Guid.NewGuid().ToString("N") : invocation.RequestId;
        var waiter = new TaskCompletionSource<ActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, waiter)) return ActionInvocationResult.Fail("duplicate-request", "The action request id is already in use.");
        try
        {
            await action.Connection.SendAsync("action.invoke", invocation with { RequestId = requestId }, requestId, cancellationToken).ConfigureAwait(false);
            var result = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new ActionInvocationResult(result.Accepted, result.Code, result.Message, result.Data);
        }
        catch (TimeoutException) { return ActionInvocationResult.Fail("timeout", "The action did not complete in time."); }
        catch (OperationCanceledException) { return ActionInvocationResult.Fail("canceled", "The action was canceled."); }
        catch (InvalidOperationException ex) { return ActionInvocationResult.Fail("unavailable", ex.Message); }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private void Host_MessageReceived(object? sender, (PluginConnection Connection, PluginEnvelope Envelope) message)
    { _ = HandleMessageAsync(message); }

    private async Task HandleMessageAsync((PluginConnection Connection, PluginEnvelope Envelope) message)
    {
        try
        {
            if (message.Envelope.Type == "action.register")
            {
                var descriptor = message.Envelope.Payload.Deserialize<ActionDescriptor>(PluginJson.Options)
                                 ?? throw new InvalidDataException("Action descriptor is invalid.");
                if (string.IsNullOrWhiteSpace(descriptor.ActionId) || descriptor.ActionId.Length > 200)
                    throw new InvalidDataException("Action id is invalid.");
                if (descriptor.ActionId == "io.github.codysimonds65.ram.macros.run" &&
                    message.Connection.PluginId != "io.github.codysimonds65.ram.macros")
                    throw new InvalidDataException("The RAM Macros action id is reserved for the official provider.");
                if (descriptor.RequiredCapabilities.Any(capability => !message.Connection.GrantedCapabilities.Contains(capability)))
                    throw new InvalidDataException("The action requires a capability the provider was not granted.");
                if (!_actions.TryAdd(descriptor.ActionId, new RegisteredAction(descriptor, message.Connection)))
                    throw new InvalidDataException($"Action '{descriptor.ActionId}' is already registered.");
                await message.Connection.SendAsync("action.registered", new { actionId = descriptor.ActionId }, message.Envelope.RequestId, CancellationToken.None).ConfigureAwait(false);
            }
            else if (message.Envelope.Type == "action.result")
            {
                var result = message.Envelope.Payload.Deserialize<ActionResult>(PluginJson.Options);
                if (result is not null && _pending.TryGetValue(message.Envelope.RequestId, out var waiter)) waiter.TrySetResult(result);
            }
            else if (message.Envelope.Type == "action.invoke")
            {
                var invocation = message.Envelope.Payload.Deserialize<ActionInvocation>(PluginJson.Options)
                                 ?? throw new InvalidDataException("Action invocation is invalid.");
                var result = await InvokeAsync(invocation).ConfigureAwait(false);
                await message.Connection.SendAsync("action.result", result, message.Envelope.RequestId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or ObjectDisposedException)
        {
            try { await message.Connection.SendAsync("action.result", ActionResult.Fail("invalid-request", ex.Message), message.Envelope.RequestId, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private void Host_Disconnected(object? sender, PluginConnection connection)
    {
        foreach (var item in _actions.Where(pair => ReferenceEquals(pair.Value.Connection, connection)).ToArray()) _actions.TryRemove(item.Key, out _);
        foreach (var waiter in _pending.Values) waiter.TrySetResult(ActionResult.Fail("disconnected", "The action provider disconnected."));
    }

    public ValueTask DisposeAsync()
    {
        _host.MessageReceived -= Host_MessageReceived;
        _host.Disconnected -= Host_Disconnected;
        _actions.Clear();
        foreach (var waiter in _pending.Values) waiter.TrySetCanceled();
        _pending.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed record RegisteredAction(ActionDescriptor Descriptor, PluginConnection Connection);
}

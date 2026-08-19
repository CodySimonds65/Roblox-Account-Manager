using System.Collections.Concurrent;

namespace RobloxAltClient.Plugins;

public sealed record BackgroundInputResult(
    bool Accepted,
    string Code,
    string Message,
    int PostedCount,
    nint ForegroundBefore,
    nint ForegroundAfter)
{
    public string DeliveryMode { get; init; } = "unknown";
    public string Verification { get; init; } = "unverified";
    public string? TraceId { get; init; }
    public int RequestedCount { get; init; }
    public nint TargetRootWindow { get; init; }
    public nint TargetRenderWindow { get; init; }
    public int TargetProcessId { get; init; }
    public long TargetProcessStartTimeUtcTicks { get; init; }
    public int CursorX { get; init; }
    public int CursorY { get; init; }
    public string? SelectedAccountId { get; init; }
    public bool? SelectedVisible { get; init; }

    public static BackgroundInputResult Failure(string code, string message, nint before, nint after, int posted = 0) =>
        new(false, code, message, posted, before, after)
        {
            DeliveryMode = "none",
            Verification = "not-delivered"
        };
}

public sealed class PriorityInputLeaseCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string accountId, string owner, int priority,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(owner, priority);
        AccountState state;
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountId, out state!)) _accounts[accountId] = state = new AccountState();
            state.Waiters.Add(waiter);
            GrantNext(state);
        }
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await waiter.Granted.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new Lease(this, accountId, state, waiter);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(state.Current, waiter))
                {
                    state.Current = null;
                    state.Active = false;
                }
                else state.Waiters.Remove(waiter);
                GrantNext(state);
                if (!state.Active && state.Waiters.Count == 0) _accounts.Remove(accountId);
            }
            return null;
        }
    }

    private void Release(string accountId, AccountState state, Waiter waiter)
    {
        lock (_gate)
        {
            if (!state.Active || !ReferenceEquals(state.Current, waiter)) return;
            state.Active = false;
            state.Current = null;
            GrantNext(state);
            if (!state.Active && state.Waiters.Count == 0) _accounts.Remove(accountId);
        }
    }

    private static void GrantNext(AccountState state)
    {
        if (state.Active || state.Waiters.Count == 0) return;
        var next = state.Waiters.OrderByDescending(waiter => waiter.Priority).ThenBy(waiter => waiter.Sequence).First();
        state.Waiters.Remove(next);
        state.Current = next;
        state.Active = true;
        next.Granted.TrySetResult(true);
    }

    private sealed class AccountState { public bool Active; public Waiter? Current; public List<Waiter> Waiters { get; } = []; }
    private sealed class Waiter(string owner, int priority)
    {
        private static long _nextSequence;
        public string Owner { get; } = owner;
        public int Priority { get; } = priority;
        public long Sequence { get; } = Interlocked.Increment(ref _nextSequence);
        public TaskCompletionSource<bool> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Lease(PriorityInputLeaseCoordinator owner, string accountId, AccountState state, Waiter waiter) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { owner.Release(accountId, state, waiter); return ValueTask.CompletedTask; }
    }
}

namespace RobloxAccountManager.Desktop;

/// <summary>
/// Coalesces passive layout refreshes while preserving explicit user selections.
/// Only one refresh runs at a time, and a selection that arrives during a passive
/// refresh is guaranteed to run next.
/// </summary>
public sealed class ClientOverlayRefreshScheduler(Func<bool, Task> refresh)
{
    private const int PassiveRequest = 1;
    private const int ExplicitRequest = 2;
    private readonly Func<bool, Task> _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _pending;

    public async Task RequestAsync(bool explicitUserSelection = false)
    {
        Interlocked.Or(ref _pending, explicitUserSelection ? ExplicitRequest : PassiveRequest);
        if (!await _gate.WaitAsync(0)) return;

        try
        {
            while (true)
            {
                var pending = Interlocked.Exchange(ref _pending, 0);
                if (pending == 0) break;
                await _refresh((pending & ExplicitRequest) != 0);
            }
        }
        finally
        {
            _gate.Release();
            if (Volatile.Read(ref _pending) != 0)
                await RequestAsync();
        }
    }

    public void ClearPending() => Interlocked.Exchange(ref _pending, 0);
}

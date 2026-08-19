using System.Diagnostics;

namespace RobloxAltClient.Services;

/// <summary>
/// Repeats singleton inspect/close/verify passes until no matching handles are
/// observed. The coordinator only invokes the supplied handle operations; it
/// never terminates or signals a Roblox process.
/// </summary>
public sealed class SingletonHandleReleaseCoordinator
{
    public async Task<SingletonSweepResult> ReleaseAsync(
        Func<CancellationToken, Task<IReadOnlyList<SingletonProcessIdentity>>> getProcessesAsync,
        Func<SingletonProcessIdentity, CancellationToken, Task<IReadOnlyList<SingletonHandleInfo>>> inspectAsync,
        Func<SingletonProcessIdentity, SingletonHandleInfo, CancellationToken, Task> closeAsync,
        int maxPasses = 3,
        CancellationToken cancellationToken = default,
        TimeSpan? retryDelay = null,
        TimeSpan? settleWindow = null)
    {
        ArgumentNullException.ThrowIfNull(getProcessesAsync);
        ArgumentNullException.ThrowIfNull(inspectAsync);
        ArgumentNullException.ThrowIfNull(closeAsync);
        if (maxPasses is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(maxPasses), "The release pass count must be between 1 and 60.");

        var pollDelay = retryDelay ?? TimeSpan.Zero;
        var quietWindow = settleWindow ?? TimeSpan.Zero;
        if (pollDelay < TimeSpan.Zero || pollDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(retryDelay), "The retry delay must be between zero and thirty seconds.");
        if (quietWindow < TimeSpan.Zero || quietWindow > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(settleWindow), "The settle window must be between zero and one minute.");

        var messages = new List<string>();
        var closedCount = 0;
        var reappeared = false;
        long? quietSinceTimestamp = null;
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = await getProcessesAsync(cancellationToken).ConfigureAwait(false);
            if (!TryGetUniqueProcesses(processes, messages, out var uniqueProcesses))
                return new SingletonSweepResult(false, closedCount, reappeared, messages);

            foreach (var process in uniqueProcesses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<SingletonHandleInfo> handles;
                try
                {
                    handles = await inspectAsync(process, cancellationToken).ConfigureAwait(false);
                }
                catch (SingletonProcessGoneException)
                {
                    // A client can exit between process enumeration and Handle.
                    // It no longer owns a live handle and must not abort the queue.
                    messages.Add($"Roblox PID {process.Pid} exited or changed identity during singleton inspection; continuing.");
                    continue;
                }

                if (handles.Count > 0)
                    quietSinceTimestamp = null;
                foreach (var handle in handles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await closeAsync(process, handle, cancellationToken).ConfigureAwait(false);
                        closedCount++;
                        quietSinceTimestamp = null;
                        messages.Add($"Released {handle.Name} in PID {process.Pid}.");
                    }
                    catch (SingletonProcessGoneException)
                    {
                        messages.Add($"Roblox PID {process.Pid} exited or changed identity while releasing a singleton handle; continuing.");
                    }
                }
            }

            var verification = await FindRemainingHandlesAsync(
                getProcessesAsync,
                inspectAsync,
                messages,
                cancellationToken).ConfigureAwait(false);
            if (verification.Conflict)
                return new SingletonSweepResult(false, closedCount, reappeared, messages);

            var remaining = verification.Handles;
            if (remaining.Count == 0)
            {
                if (quietWindow <= TimeSpan.Zero)
                    return new SingletonSweepResult(true, closedCount, reappeared, messages);

                quietSinceTimestamp ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(quietSinceTimestamp.Value) >= quietWindow)
                    return new SingletonSweepResult(true, closedCount, reappeared, messages);

                if (pass == maxPasses)
                {
                    messages.Add($"Singleton quiet-window polling reached {maxPasses} passes before a full {quietWindow.TotalSeconds:0.#}-second quiet interval was observed.");
                    return new SingletonSweepResult(false, closedCount, reappeared, messages);
                }

                await DelayBetweenPassesAsync(pollDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            reappeared = true;
            quietSinceTimestamp = null;
            if (pass == maxPasses)
            {
                messages.Add($"Singleton handles remained after {maxPasses} release passes.");
                return new SingletonSweepResult(false, closedCount, true, messages);
            }

            messages.Add("A singleton handle reappeared; retrying the unlock pass...");
            await DelayBetweenPassesAsync(pollDelay, cancellationToken).ConfigureAwait(false);
        }

        return new SingletonSweepResult(false, closedCount, reappeared, messages);
    }

    private static Task DelayBetweenPassesAsync(TimeSpan pollDelay, CancellationToken cancellationToken)
    {
        // A small default yield keeps a caller that requested a quiet window
        // from spinning Handle64 at full speed while preserving the existing
        // zero-delay behavior for unit tests and one-shot manual releases.
        return Task.Delay(
            pollDelay > TimeSpan.Zero ? pollDelay : TimeSpan.FromMilliseconds(100),
            cancellationToken);
    }

    private static async Task<(IReadOnlyList<(int ProcessId, SingletonHandleInfo Handle)> Handles, bool Conflict)> FindRemainingHandlesAsync(
        Func<CancellationToken, Task<IReadOnlyList<SingletonProcessIdentity>>> getProcessesAsync,
        Func<SingletonProcessIdentity, CancellationToken, Task<IReadOnlyList<SingletonHandleInfo>>> inspectAsync,
        ICollection<string> messages,
        CancellationToken cancellationToken)
    {
        var remaining = new List<(int, SingletonHandleInfo)>();
        var processes = await getProcessesAsync(cancellationToken).ConfigureAwait(false);
        if (!TryGetUniqueProcesses(processes, messages, out var uniqueProcesses))
            return (remaining, true);

        foreach (var process in uniqueProcesses)
        {
            try
            {
                var handles = await inspectAsync(process, cancellationToken).ConfigureAwait(false);
                remaining.AddRange(handles.Select(handle => (process.Pid, handle)));
            }
            catch (SingletonProcessGoneException)
            {
                messages.Add($"Roblox PID {process.Pid} exited or changed identity during singleton verification; continuing.");
            }
        }

        return (remaining, false);
    }

    private static bool TryGetUniqueProcesses(
        IReadOnlyList<SingletonProcessIdentity> processes,
        ICollection<string> messages,
        out IReadOnlyList<SingletonProcessIdentity> uniqueProcesses)
    {
        var unique = new List<SingletonProcessIdentity>();
        foreach (var group in processes.GroupBy(process => process.Pid))
        {
            var identities = group.Distinct().ToArray();
            if (identities.Length > 1)
            {
                messages.Add($"Conflicting identities were observed for Roblox PID {group.Key}; singleton cleanup stopped without closing a handle.");
                uniqueProcesses = [];
                return false;
            }

            if (identities.Length == 1)
                unique.Add(identities[0]);
        }

        uniqueProcesses = unique;
        return true;
    }
}

public sealed record SingletonHandleInfo(string Id, string Name);

/// <summary>
/// Identity captured for a Roblox process before Handle64 is invoked. A PID by
/// itself is not sufficient because Windows can recycle it while a sweep is in
/// progress.
/// </summary>
public sealed record SingletonProcessIdentity(
    int Pid,
    long StartTimeUtcTicks,
    string ExecutablePath);

public sealed record SingletonSweepResult(
    bool Success,
    int ClosedCount,
    bool HadReappearingHandles,
    IReadOnlyList<string> Messages);

/// <summary>Raised when a process disappears between Handle operations.</summary>
public sealed class SingletonProcessGoneException : Exception
{
    public SingletonProcessGoneException(int processId)
        : base($"Roblox PID {processId} is no longer running.")
    {
        ProcessId = processId;
    }

    public int ProcessId { get; }
}

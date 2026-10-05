namespace RobloxAltClient.Services;

/// <summary>
/// A Roblox player can exist briefly, exit for a required update, and be
/// replaced by the installer. Only a continuously observed identity survives
/// the startup check; a gap, replacement, or ambiguous process list resets it.
/// This is a liveness check, not confirmation that the client joined a game.
/// </summary>
internal sealed class RobloxStartupTracker<TIdentity> where TIdentity : struct
{
    internal static readonly TimeSpan StartupInterval = TimeSpan.FromSeconds(8);
    private TIdentity? _candidate;
    private TimeSpan _firstObservedAt;

    public bool Observe(TIdentity? uniqueCandidate, TimeSpan observedAt)
    {
        if (uniqueCandidate is null)
        {
            _candidate = null;
            return false;
        }

        if (!EqualityComparer<TIdentity?>.Default.Equals(_candidate, uniqueCandidate))
        {
            _candidate = uniqueCandidate;
            _firstObservedAt = observedAt;
        }

        return observedAt - _firstObservedAt >= StartupInterval;
    }
}

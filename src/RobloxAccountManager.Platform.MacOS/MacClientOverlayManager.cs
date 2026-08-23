using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacOverlayOperationResult(bool Succeeded, string DiagnosticCode)
{
    public static MacOverlayOperationResult Success() => new(true, "overlay-ready");
    public static MacOverlayOperationResult Failure(string code) => new(false, code);
}

/// <summary>
/// Serializes the state of external Roblox windows placed over the Clients
/// viewport. Window frames are changed without activation; raising occurs only
/// when the caller marks an operation as an explicit user selection.
/// </summary>
public sealed class MacClientOverlayManager
{
    private readonly IRobloxProcessLocator _processLocator;
    private readonly IMacAccessibilityApi _accessibility;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TrackedWindow> _tracked = new(StringComparer.Ordinal);

    public MacClientOverlayManager(
        IRobloxProcessLocator? processLocator = null,
        IMacAccessibilityApi? accessibility = null)
    {
        _processLocator = processLocator ?? new MacRobloxProcessLocator();
        _accessibility = accessibility ?? new MacAccessibilityApi();
    }

    public MacCapabilityResult GetCapability() => _accessibility.GetCapability();

    public async ValueTask<MacOverlayOperationResult> ShowOnlyAsync(
        IReadOnlyList<Contracts.RobloxWindowInfo> eligibleWindows,
        string selectedAccountId,
        MacWindowFrame viewport,
        bool explicitUserSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligibleWindows);
        if (string.IsNullOrWhiteSpace(selectedAccountId) || !viewport.IsValid)
            return MacOverlayOperationResult.Failure("invalid-overlay-request");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var capability = _accessibility.GetCapability();
            if (!capability.IsSupported) return MacOverlayOperationResult.Failure(capability.Code);

            var candidates = new List<(Contracts.RobloxWindowInfo Window, MacAccessibleWindow Accessible)>();
            foreach (var window in eligibleWindows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(window.AccountId) || !IsCurrentManagedIdentity(window.Process))
                    return await FailAndRestoreAsync("stale-process-identity", cancellationToken).ConfigureAwait(false);
                var accessible = _accessibility.FindMainWindow(window.Process.Pid);
                if (accessible is null)
                    return await FailAndRestoreAsync("accessible-window-not-ready", cancellationToken).ConfigureAwait(false);
                if (accessible.IsFullScreen)
                    return await FailAndRestoreAsync("fullscreen-window-not-supported", cancellationToken).ConfigureAwait(false);

                if (_tracked.TryGetValue(window.AccountId, out var tracked)
                    && (!tracked.Process.Matches(window.Process)
                        || !string.Equals(tracked.WindowIdentifier, accessible.Identifier, StringComparison.Ordinal)))
                {
                    return await FailAndRestoreAsync("accessible-window-changed", cancellationToken).ConfigureAwait(false);
                }
                if (!_tracked.ContainsKey(window.AccountId))
                {
                    _tracked[window.AccountId] = new TrackedWindow(
                        window.Process,
                        accessible.Identifier,
                        accessible.Frame,
                        accessible.IsMinimized);
                }
                candidates.Add((window, accessible));
            }

            var selected = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Window.AccountId, selectedAccountId, StringComparison.Ordinal));
            if (selected.Window is null)
                return await FailAndRestoreAsync("selected-account-not-running", cancellationToken).ConfigureAwait(false);

            foreach (var candidate in candidates.Where(candidate =>
                         !string.Equals(candidate.Window.AccountId, selectedAccountId, StringComparison.Ordinal)))
            {
                if (!_accessibility.TrySetMinimized(
                        candidate.Window.Process.Pid,
                        candidate.Accessible.Identifier,
                        true))
                {
                    return await FailAndRestoreAsync("hide-unselected-failed", cancellationToken).ConfigureAwait(false);
                }
            }

            if (!_accessibility.TrySetFrame(selected.Window.Process.Pid, selected.Accessible.Identifier, viewport)
                || !_accessibility.TrySetMinimized(selected.Window.Process.Pid, selected.Accessible.Identifier, false))
            {
                return await FailAndRestoreAsync("show-selected-failed", cancellationToken).ConfigureAwait(false);
            }

            if (explicitUserSelection
                && !_accessibility.TryRaise(selected.Window.Process.Pid, selected.Accessible.Identifier))
            {
                return await FailAndRestoreAsync("raise-selected-failed", cancellationToken).ConfigureAwait(false);
            }

            return MacOverlayOperationResult.Success();
        }
        catch
        {
            try { _ = RestoreAll(); }
            catch { /* Preserve the original failure after best-effort restoration. */ }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<MacOverlayOperationResult> HideAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var tracked in _tracked.Values.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentManagedIdentity(tracked.Process))
                {
                    RemoveTrackedIdentity(tracked.Process);
                    continue;
                }
                if (!_accessibility.TrySetMinimized(tracked.Process.Pid, tracked.WindowIdentifier, true))
                    return MacOverlayOperationResult.Failure("hide-overlay-failed");
            }
            return MacOverlayOperationResult.Success();
        }
        catch
        {
            try { _ = RestoreAll(); }
            catch { /* Preserve the original failure after best-effort restoration. */ }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<MacOverlayOperationResult> RestoreAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tracked.Remove(accountId, out var tracked)) return MacOverlayOperationResult.Success();
            return Restore(tracked)
                ? MacOverlayOperationResult.Success()
                : MacOverlayOperationResult.Failure("restore-overlay-failed");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<MacOverlayOperationResult> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return RestoreAll(); }
        finally { _gate.Release(); }
    }

    private ValueTask<MacOverlayOperationResult> FailAndRestoreAsync(
        string code,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = RestoreAll();
        return ValueTask.FromResult(MacOverlayOperationResult.Failure(code));
    }

    private MacOverlayOperationResult RestoreAll()
    {
        var succeeded = true;
        foreach (var tracked in _tracked.Values.ToArray()) succeeded &= Restore(tracked);
        _tracked.Clear();
        return succeeded
            ? MacOverlayOperationResult.Success()
            : MacOverlayOperationResult.Failure("restore-overlay-failed");
    }

    private bool Restore(TrackedWindow tracked)
    {
        if (!IsCurrentManagedIdentity(tracked.Process)) return true;
        var current = _accessibility.FindMainWindow(tracked.Process.Pid);
        if (current is null || !string.Equals(current.Identifier, tracked.WindowIdentifier, StringComparison.Ordinal))
            return false;
        var frameRestored = _accessibility.TrySetFrame(
            tracked.Process.Pid,
            tracked.WindowIdentifier,
            tracked.OriginalFrame);
        var minimizedRestored = _accessibility.TrySetMinimized(
            tracked.Process.Pid,
            tracked.WindowIdentifier,
            tracked.OriginallyMinimized);
        return frameRestored && minimizedRestored;
    }

    private bool IsCurrentManagedIdentity(Contracts.RobloxProcessIdentity expected)
    {
        if (expected.Platform != Contracts.RobloxPlatform.MacOS || !expected.IsValid) return false;
        var current = _processLocator.FindProcess(expected.Pid);
        return current is not null && current.IsManaged
            && _processLocator.IsSameProcess(MacCoreProcessLocator.FromCore(expected), current);
    }

    private void RemoveTrackedIdentity(Contracts.RobloxProcessIdentity identity)
    {
        foreach (var accountId in _tracked
                     .Where(pair => pair.Value.Process.Matches(identity))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _tracked.Remove(accountId);
        }
    }

    private sealed record TrackedWindow(
        Contracts.RobloxProcessIdentity Process,
        string WindowIdentifier,
        MacWindowFrame OriginalFrame,
        bool OriginallyMinimized);
}

public static class MacViewportCoordinateConverter
{
    public static MacWindowFrame FromAvaloniaPixels(
        int screenPixelX,
        int screenPixelY,
        double widthInDips,
        double heightInDips,
        double renderScaling)
    {
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
            throw new ArgumentOutOfRangeException(nameof(renderScaling));
        return new MacWindowFrame(
            screenPixelX / renderScaling,
            screenPixelY / renderScaling,
            widthInDips,
            heightInDips);
    }
}

using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacOverlayClientDiagnostic(
    string AccountId,
    int ProcessId,
    string DiagnosticCode,
    bool IsReady,
    int TotalWindowCount,
    int EligibleWindowCount);

public sealed record MacOverlayOperationResult(
    bool Succeeded,
    string DiagnosticCode,
    string? AccountId,
    int? ProcessId,
    IReadOnlyList<MacOverlayClientDiagnostic> Clients)
{
    public static MacOverlayOperationResult Success(
        IReadOnlyList<MacOverlayClientDiagnostic>? clients = null,
        string? accountId = null,
        int? processId = null) =>
        new(true, "overlay-ready", accountId, processId, clients ?? []);

    public static MacOverlayOperationResult Failure(
        string code,
        string? accountId = null,
        int? processId = null,
        IReadOnlyList<MacOverlayClientDiagnostic>? clients = null) =>
        new(false, code, accountId, processId, clients ?? []);
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
        _accessibility = accessibility ?? new MacAccessibilityApi(_processLocator);
    }

    public MacCapabilityResult GetCapability() => _accessibility.GetCapability();

    public async ValueTask<MacOverlayOperationResult> ShowOnlyAsync(
        IReadOnlyList<Contracts.RobloxWindowInfo> eligibleWindows,
        string selectedAccountId,
        MacWindowFrame viewport,
        bool explicitUserSelection,
        Func<bool>? canRaise = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligibleWindows);
        if (string.IsNullOrWhiteSpace(selectedAccountId) || !viewport.IsValid)
            return MacOverlayOperationResult.Failure("invalid-overlay-request");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var capability = _accessibility.GetCapability();
            if (!capability.IsSupported)
            {
                var unavailable = eligibleWindows
                    .Where(window => !string.IsNullOrWhiteSpace(window.AccountId))
                    .Select(window => Diagnostic(window, capability.Code, ready: false))
                    .ToArray();
                return MacOverlayOperationResult.Failure(capability.Code, clients: unavailable);
            }

            var candidates = new List<(Contracts.RobloxWindowInfo Window, MacAccessibleWindow Accessible)>();
            var diagnostics = new List<MacOverlayClientDiagnostic>();
            foreach (var window in eligibleWindows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(window.AccountId) || !IsCurrentManagedIdentity(window.Process))
                {
                    diagnostics.Add(Diagnostic(window, "stale-process-identity", ready: false));
                    continue;
                }

                var probe = _accessibility.ProbeMainWindow(window.Process);
                var accessible = probe.Window;
                if (accessible is null)
                {
                    diagnostics.Add(Diagnostic(window, probe.DiagnosticCode, ready: false, probe));
                    continue;
                }
                if (accessible.IsFullScreen)
                {
                    if (!IsTracked(window.Process, accessible.Identifier))
                        _accessibility.ForgetWindow(window.Process, accessible.Identifier);
                    diagnostics.Add(Diagnostic(window, "fullscreen-window-not-supported", ready: false, probe));
                    continue;
                }

                if (_tracked.TryGetValue(window.AccountId, out var tracked)
                    && (!tracked.Process.Matches(window.Process)
                        || !string.Equals(tracked.WindowIdentifier, accessible.Identifier, StringComparison.Ordinal)))
                {
                    if (tracked.Process.Matches(window.Process))
                    {
                        _tracked.Remove(window.AccountId);
                        _accessibility.ForgetWindow(tracked.Process, tracked.WindowIdentifier);
                    }
                    diagnostics.Add(Diagnostic(window, "accessible-window-changed", ready: false, probe));
                    continue;
                }

                diagnostics.Add(Diagnostic(window, "accessible-window-ready", ready: true, probe));
                candidates.Add((window, accessible));
            }

            var unavailableClient = diagnostics.FirstOrDefault(diagnostic => !diagnostic.IsReady);
            if (unavailableClient is not null)
            {
                return await FailAndRestoreAsync(
                    unavailableClient.DiagnosticCode,
                    cancellationToken,
                    unavailableClient.AccountId,
                    unavailableClient.ProcessId,
                    diagnostics).ConfigureAwait(false);
            }

            var selected = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Window.AccountId, selectedAccountId, StringComparison.Ordinal));
            if (selected.Window is null)
                return await FailAndRestoreAsync(
                    "selected-account-not-running",
                    cancellationToken,
                    selectedAccountId,
                    clients: diagnostics).ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                var accountId = candidate.Window.AccountId!;
                if (!_tracked.ContainsKey(accountId))
                {
                    _tracked[accountId] = new TrackedWindow(
                        candidate.Window.Process,
                        candidate.Accessible.Identifier,
                        candidate.Accessible.Frame,
                        candidate.Accessible.IsMinimized);
                }
            }

            var hiddenAllUnselected = true;
            foreach (var candidate in candidates.Where(candidate =>
                         !string.Equals(candidate.Window.AccountId, selectedAccountId, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                hiddenAllUnselected &= TrySetMinimizedVerified(
                    candidate.Window.Process,
                    candidate.Accessible.Identifier,
                    true);
            }
            if (!hiddenAllUnselected)
                return await FailAndRestoreAsync(
                    "hide-unselected-failed",
                    cancellationToken,
                    clients: diagnostics).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySetFrameVerified(selected.Window.Process, selected.Accessible.Identifier, viewport)
                || !TrySetMinimizedVerified(selected.Window.Process, selected.Accessible.Identifier, false))
            {
                return await FailAndRestoreAsync(
                    "show-selected-failed",
                    cancellationToken,
                    selectedAccountId,
                    selected.Window.Process.Pid,
                    diagnostics).ConfigureAwait(false);
            }

            if (explicitUserSelection && canRaise is null)
                return MacOverlayOperationResult.Failure(
                    "raise-cancelled",
                    selectedAccountId,
                    selected.Window.Process.Pid,
                    diagnostics);
            cancellationToken.ThrowIfCancellationRequested();
            if (explicitUserSelection
                && (!_accessibility.TryRaise(selected.Window.Process, selected.Accessible.Identifier, canRaise!)
                    || !IsCurrentManagedIdentity(selected.Window.Process)))
            {
                if (!canRaise!())
                    return MacOverlayOperationResult.Failure(
                        "raise-cancelled",
                        selectedAccountId,
                        selected.Window.Process.Pid,
                        diagnostics);
                return await FailAndRestoreAsync(
                    "raise-selected-failed",
                    cancellationToken,
                    selectedAccountId,
                    selected.Window.Process.Pid,
                    diagnostics).ConfigureAwait(false);
            }

            return MacOverlayOperationResult.Success(
                diagnostics,
                selectedAccountId,
                selected.Window.Process.Pid);
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
            var succeeded = true;
            foreach (var tracked in _tracked.Values.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentManagedIdentity(tracked.Process))
                {
                    RemoveTrackedIdentity(tracked.Process);
                    continue;
                }
                succeeded &= TrySetMinimizedVerified(tracked.Process, tracked.WindowIdentifier, true);
            }
            return succeeded
                ? MacOverlayOperationResult.Success()
                : MacOverlayOperationResult.Failure("hide-overlay-failed");
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
            if (!_tracked.TryGetValue(accountId, out var tracked)) return MacOverlayOperationResult.Success();
            if (!Restore(tracked)) return MacOverlayOperationResult.Failure("restore-overlay-failed");
            _tracked.Remove(accountId);
            _accessibility.ForgetWindow(tracked.Process, tracked.WindowIdentifier);
            return MacOverlayOperationResult.Success();
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
        CancellationToken cancellationToken,
        string? accountId = null,
        int? processId = null,
        IReadOnlyList<MacOverlayClientDiagnostic>? clients = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restore = RestoreAll();
        return ValueTask.FromResult(MacOverlayOperationResult.Failure(
            restore.Succeeded ? code : $"{code}:restore-overlay-failed",
            accountId,
            processId,
            clients));
    }

    private static MacOverlayClientDiagnostic Diagnostic(
        Contracts.RobloxWindowInfo window,
        string code,
        bool ready,
        MacAccessibilityWindowProbe? probe = null) =>
        new(
            window.AccountId ?? string.Empty,
            window.Process.Pid,
            code,
            ready,
            probe?.TotalWindowCount ?? 0,
            probe?.EligibleWindowCount ?? 0);

    private MacOverlayOperationResult RestoreAll()
    {
        var succeeded = true;
        foreach (var pair in _tracked.ToArray())
        {
            if (Restore(pair.Value))
            {
                _tracked.Remove(pair.Key);
                _accessibility.ForgetWindow(pair.Value.Process, pair.Value.WindowIdentifier);
            }
            else
                succeeded = false;
        }
        return succeeded
            ? MacOverlayOperationResult.Success()
            : MacOverlayOperationResult.Failure("restore-overlay-failed");
    }

    private bool Restore(TrackedWindow tracked)
    {
        if (!IsCurrentManagedIdentity(tracked.Process)) return true;
        var current = _accessibility.ProbeMainWindow(tracked.Process).Window;
        if (current is null || !string.Equals(current.Identifier, tracked.WindowIdentifier, StringComparison.Ordinal))
            return false;
        var frameRestored = TrySetFrameVerified(
            tracked.Process,
            tracked.WindowIdentifier,
            tracked.OriginalFrame);
        var minimizedRestored = TrySetMinimizedVerified(
            tracked.Process,
            tracked.WindowIdentifier,
            tracked.OriginallyMinimized);
        return frameRestored && minimizedRestored;
    }

    private bool TrySetFrameVerified(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        MacWindowFrame frame)
    {
        if (!IsCurrentManagedIdentity(process)
            || !_accessibility.TrySetFrame(process, windowIdentifier, frame)
            || !IsCurrentManagedIdentity(process)) return false;
        var current = _accessibility.ProbeMainWindow(process).Window;
        return current is not null
            && string.Equals(current.Identifier, windowIdentifier, StringComparison.Ordinal)
            && FramesMatch(current.Frame, frame);
    }

    private bool TrySetMinimizedVerified(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        bool minimized)
    {
        if (!IsCurrentManagedIdentity(process)
            || !_accessibility.TrySetMinimized(process, windowIdentifier, minimized)
            || !IsCurrentManagedIdentity(process)) return false;
        var current = _accessibility.ProbeMainWindow(process).Window;
        return current is not null
            && string.Equals(current.Identifier, windowIdentifier, StringComparison.Ordinal)
            && current.IsMinimized == minimized;
    }

    private static bool FramesMatch(MacWindowFrame left, MacWindowFrame right) =>
        Math.Abs(left.Left - right.Left) <= 1
        && Math.Abs(left.Top - right.Top) <= 1
        && Math.Abs(left.Width - right.Width) <= 1
        && Math.Abs(left.Height - right.Height) <= 1;

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
            var tracked = _tracked[accountId];
            _tracked.Remove(accountId);
            _accessibility.ForgetWindow(tracked.Process, tracked.WindowIdentifier);
        }
    }

    private bool IsTracked(Contracts.RobloxProcessIdentity process, string windowIdentifier) =>
        _tracked.Values.Any(tracked => tracked.Process.Matches(process)
            && string.Equals(tracked.WindowIdentifier, windowIdentifier, StringComparison.Ordinal));

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

using System.Diagnostics;
using Contracts = RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacOverlayClientDiagnostic(
    string AccountId,
    int ProcessId,
    string DiagnosticCode,
    bool IsReady,
    int TotalWindowCount,
    int EligibleWindowCount,
    string Phase = "preflight",
    bool Retryable = false);

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
    private static readonly TimeSpan AccessibilitySettleTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AccessibilitySettleInterval = TimeSpan.FromMilliseconds(50);
    private readonly IRobloxProcessLocator _processLocator;
    private readonly IMacAccessibilityApi _accessibility;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TrackedWindow> _tracked = new(StringComparer.Ordinal);

    private enum ProcessIdentityState
    {
        Current,
        Gone,
        Changed
    }

    private readonly record struct OverlayStateResult(bool Succeeded, string DiagnosticCode, bool Retryable)
    {
        public static OverlayStateResult Success(string diagnosticCode) => new(true, diagnosticCode, false);
        public static OverlayStateResult Failure(string diagnosticCode, bool retryable = false) =>
            new(false, diagnosticCode, retryable);
    }

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
                    if (tracked.Process.Matches(window.Process)
                        && !IsTracked(window.Process, accessible.Identifier))
                    {
                        _accessibility.ForgetWindow(window.Process, accessible.Identifier);
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

            foreach (var candidate in candidates.Where(candidate =>
                         !string.Equals(candidate.Window.AccountId, selectedAccountId, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hideResult = await TrySetMinimizedVerifiedAsync(
                    candidate.Window.Process,
                    candidate.Accessible.Identifier,
                    true,
                    cancellationToken).ConfigureAwait(false);
                if (hideResult.Succeeded) continue;
                return await FailAndRestoreAsync(
                    $"hide-unselected-{hideResult.DiagnosticCode}",
                    cancellationToken,
                    candidate.Window.AccountId,
                    candidate.Window.Process.Pid,
                    diagnostics).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Position and size are only guaranteed AX attributes for visible
            // objects. Unminimize first so a Dock-minimized Roblox window can be
            // measured and placed instead of failing before it becomes visible.
            var selectedVisibility = await TrySetMinimizedVerifiedAsync(
                    selected.Window.Process,
                    selected.Accessible.Identifier,
                    false,
                    cancellationToken).ConfigureAwait(false);
            var selectedFrame = selectedVisibility.Succeeded
                ? await TrySetFrameVerifiedAsync(
                    selected.Window.Process,
                    selected.Accessible.Identifier,
                    viewport,
                    cancellationToken).ConfigureAwait(false)
                : selectedVisibility;
            if (!selectedFrame.Succeeded)
            {
                return await FailAndRestoreAsync(
                    $"show-selected-{selectedFrame.DiagnosticCode}",
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
            try { _ = await RestoreAllCoreAsync(CancellationToken.None).ConfigureAwait(false); }
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
                var result = await TrySetMinimizedVerifiedAsync(
                    tracked.Process,
                    tracked.WindowIdentifier,
                    true,
                    cancellationToken).ConfigureAwait(false);
                succeeded &= result.Succeeded;
            }
            return succeeded
                ? MacOverlayOperationResult.Success()
                : MacOverlayOperationResult.Failure("hide-overlay-failed");
        }
        catch
        {
            try { _ = await RestoreAllCoreAsync(CancellationToken.None).ConfigureAwait(false); }
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
            var restore = await RestoreTrackedAsync(tracked, cancellationToken).ConfigureAwait(false);
            if (!restore.Succeeded)
            {
                return MacOverlayOperationResult.Failure(
                    "restore-overlay-failed",
                    accountId,
                    tracked.Process.Pid,
                    [new MacOverlayClientDiagnostic(
                        accountId,
                        tracked.Process.Pid,
                        restore.DiagnosticCode,
                        false,
                        0,
                        0,
                        "restore",
                        restore.Retryable)]);
            }
            _tracked.Remove(accountId);
            _accessibility.ForgetWindow(tracked.Process, tracked.WindowIdentifier);
            return MacOverlayOperationResult.Success();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<MacOverlayOperationResult> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RestoreAllCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async ValueTask<MacOverlayOperationResult> FailAndRestoreAsync(
        string code,
        CancellationToken cancellationToken,
        string? accountId = null,
        int? processId = null,
        IReadOnlyList<MacOverlayClientDiagnostic>? clients = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restore = await RestoreAllCoreAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = (clients ?? [])
            .Concat(restore.Clients)
            .ToArray();
        return MacOverlayOperationResult.Failure(
            restore.Succeeded ? code : $"{code}:restore-overlay-failed",
            accountId ?? restore.AccountId,
            processId ?? restore.ProcessId,
            diagnostics);
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

    private async Task<MacOverlayOperationResult> RestoreAllCoreAsync(CancellationToken cancellationToken)
    {
        var failures = new List<MacOverlayClientDiagnostic>();
        foreach (var pair in _tracked.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var restore = await RestoreTrackedAsync(pair.Value, cancellationToken).ConfigureAwait(false);
            if (restore.Succeeded)
            {
                _tracked.Remove(pair.Key);
                _accessibility.ForgetWindow(pair.Value.Process, pair.Value.WindowIdentifier);
            }
            else
            {
                failures.Add(new MacOverlayClientDiagnostic(
                    pair.Key,
                    pair.Value.Process.Pid,
                    restore.DiagnosticCode,
                    false,
                    0,
                    0,
                    "restore",
                    restore.Retryable));
            }
        }
        return failures.Count == 0
            ? MacOverlayOperationResult.Success()
            : MacOverlayOperationResult.Failure(
                "restore-overlay-failed",
                failures[0].AccountId,
                failures[0].ProcessId,
                failures);
    }

    private async Task<OverlayStateResult> RestoreTrackedAsync(
        TrackedWindow tracked,
        CancellationToken cancellationToken)
    {
        var identityState = GetProcessIdentityState(tracked.Process);
        if (identityState == ProcessIdentityState.Gone)
            return OverlayStateResult.Success("process-exited");
        if (identityState != ProcessIdentityState.Current)
            return OverlayStateResult.Failure("stale-process-identity");
        var currentProbe = _accessibility.ProbeMainWindow(tracked.Process);
        var current = currentProbe.Window;
        if (current is null)
        {
            if (!IsRetryableProbeCode(currentProbe.DiagnosticCode))
                return OverlayStateResult.Failure(currentProbe.DiagnosticCode);

            // A minimized window can disappear from AXWindows even though its
            // retained AXUIElement remains valid. Restore visibility through
            // the tracked identifier before requiring another enumeration.
            var madeVisible = await TrySetMinimizedVerifiedAsync(
                tracked.Process,
                tracked.WindowIdentifier,
                false,
                cancellationToken).ConfigureAwait(false);
            if (!madeVisible.Succeeded) return madeVisible;

            current = _accessibility.ProbeMainWindow(tracked.Process).Window;
            if (current is null)
                return OverlayStateResult.Failure("accessibility-window-readback-unavailable", true);
        }
        if (!string.Equals(current.Identifier, tracked.WindowIdentifier, StringComparison.Ordinal))
            return OverlayStateResult.Failure("accessible-window-changed");
        var frameRestored = FramesMatch(current.Frame, tracked.OriginalFrame);
        if (!frameRestored)
        {
            // Position and size are only guaranteed for a visible AX object.
            // Avoid this unminimize/write cycle entirely when the frame never
            // changed (for example, when Roblox rejected the initial resize).
            var madeVisible = await TrySetMinimizedVerifiedAsync(
                tracked.Process,
                tracked.WindowIdentifier,
                false,
                cancellationToken).ConfigureAwait(false);
            if (!madeVisible.Succeeded) return madeVisible;
            var frameResult = await TrySetFrameVerifiedAsync(
                tracked.Process,
                tracked.WindowIdentifier,
                tracked.OriginalFrame,
                cancellationToken).ConfigureAwait(false);
            if (!frameResult.Succeeded) return frameResult;
            frameRestored = true;
        }
        var minimizedRestored = await TrySetMinimizedVerifiedAsync(
            tracked.Process,
            tracked.WindowIdentifier,
            tracked.OriginallyMinimized,
            cancellationToken).ConfigureAwait(false);
        return frameRestored && minimizedRestored.Succeeded
            ? OverlayStateResult.Success("restore-overlay-ready")
            : minimizedRestored;
    }

    private async Task<OverlayStateResult> TrySetFrameVerifiedAsync(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        MacWindowFrame frame,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentManagedIdentity(process))
            return OverlayStateResult.Failure("accessibility-frame-stale-process-identity");
        var beforeProbe = _accessibility.ProbeMainWindow(process);
        var before = beforeProbe.Window;
        if (before is null && !IsRetryableProbeCode(beforeProbe.DiagnosticCode))
            return OverlayStateResult.Failure(beforeProbe.DiagnosticCode);
        if (before is not null
            && !string.Equals(before.Identifier, windowIdentifier, StringComparison.Ordinal))
            return OverlayStateResult.Failure("accessibility-frame-window-changed");
        if (before is not null && FramesMatch(before.Frame, frame))
        {
            return OverlayStateResult.Success("accessibility-frame-already-ready");
        }
        var operation = _accessibility.TrySetFrame(process, windowIdentifier, frame);
        if (!operation.Succeeded)
            return OverlayStateResult.Failure(operation.DiagnosticCode);
        if (!IsCurrentManagedIdentity(process))
            return OverlayStateResult.Failure("accessibility-frame-stale-process-identity");
        return await WaitForWindowStateAsync(
            process,
            windowIdentifier,
            window => FramesMatch(window.Frame, frame),
            "accessibility-frame-ready",
            "accessibility-frame-readback-mismatch",
            "accessibility-frame-readback-unavailable",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OverlayStateResult> TrySetMinimizedVerifiedAsync(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        bool minimized,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentManagedIdentity(process))
            return OverlayStateResult.Failure("accessibility-minimized-stale-process-identity");
        var beforeProbe = _accessibility.ProbeMainWindow(process);
        var before = beforeProbe.Window;
        if (before is null && !IsRetryableProbeCode(beforeProbe.DiagnosticCode))
            return OverlayStateResult.Failure(beforeProbe.DiagnosticCode);
        if (before is not null
            && !string.Equals(before.Identifier, windowIdentifier, StringComparison.Ordinal))
            return OverlayStateResult.Failure("accessibility-minimized-window-changed");
        if (before is not null
            && before.IsMinimized == minimized)
        {
            return OverlayStateResult.Success("accessibility-minimized-already-ready");
        }
        var operation = _accessibility.TrySetMinimized(process, windowIdentifier, minimized);
        if (!operation.Succeeded)
            return OverlayStateResult.Failure(operation.DiagnosticCode);
        if (!IsCurrentManagedIdentity(process))
            return OverlayStateResult.Failure("accessibility-minimized-stale-process-identity");
        return await WaitForWindowStateAsync(
            process,
            windowIdentifier,
            window => window.IsMinimized == minimized,
            "accessibility-minimized-ready",
            "accessibility-minimized-readback-mismatch",
            "accessibility-minimized-readback-unavailable",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OverlayStateResult> WaitForWindowStateAsync(
        Contracts.RobloxProcessIdentity process,
        string windowIdentifier,
        Func<MacAccessibleWindow, bool> predicate,
        string readyCode,
        string mismatchCode,
        string unavailableCode,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)(AccessibilitySettleTimeout.TotalSeconds * Stopwatch.Frequency);
        var lastCode = unavailableCode;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentManagedIdentity(process))
                return OverlayStateResult.Failure("stale-process-identity");
            var probe = _accessibility.ProbeMainWindow(process);
            if (probe.Window is not null)
            {
                if (!string.Equals(probe.Window.Identifier, windowIdentifier, StringComparison.Ordinal))
                    return OverlayStateResult.Failure("accessible-window-changed");
                if (predicate(probe.Window))
                    return OverlayStateResult.Success(readyCode);
                lastCode = mismatchCode;
            }
            else
            {
                lastCode = probe.DiagnosticCode;
                if (!IsRetryableProbeCode(lastCode))
                    return OverlayStateResult.Failure(lastCode);
            }
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return OverlayStateResult.Failure(lastCode, retryable: true);
            }
            await Task.Delay(AccessibilitySettleInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryableProbeCode(string code) =>
        code is "accessibility-window-not-settled"
            or "accessibility-no-eligible-window"
            or "accessibility-no-windows"
            or "accessibility-application-unavailable";

    private static bool FramesMatch(MacWindowFrame left, MacWindowFrame right) =>
        Math.Abs(left.Left - right.Left) <= 1
        && Math.Abs(left.Top - right.Top) <= 1
        && Math.Abs(left.Width - right.Width) <= 1
        && Math.Abs(left.Height - right.Height) <= 1;

    private bool IsCurrentManagedIdentity(Contracts.RobloxProcessIdentity expected)
    {
        return GetProcessIdentityState(expected) == ProcessIdentityState.Current;
    }

    private ProcessIdentityState GetProcessIdentityState(Contracts.RobloxProcessIdentity expected)
    {
        if (expected.Platform != Contracts.RobloxPlatform.MacOS || !expected.IsValid)
            return ProcessIdentityState.Changed;
        var current = _processLocator.FindProcess(expected.Pid);
        if (current is null) return ProcessIdentityState.Gone;
        return current.IsManaged
            && _processLocator.IsSameProcess(MacCoreProcessLocator.FromCore(expected), current)
            ? ProcessIdentityState.Current
            : ProcessIdentityState.Changed;
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

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacWindowOperationResult(
    bool Succeeded,
    MacCapabilityResult Capability,
    string? FailureReason)
{
    public static MacWindowOperationResult PermissionDenied() => new(
        false,
        MacCapabilityResult.PermissionRequired(
            "Window focus and tiling require Accessibility permission in System Settings > Privacy & Security > Accessibility."),
        "Accessibility permission was not granted.");
}

public interface IClientWindowManager
{
    MacCapabilityResult GetCapability();

    Task<MacWindowOperationResult> FocusAsync(
        RobloxProcessIdentity process,
        CancellationToken cancellationToken = default);

    Task<MacWindowOperationResult> TileAsync(
        IReadOnlyList<RobloxProcessIdentity> processes,
        CancellationToken cancellationToken = default);

    Task<bool> CloseVerifiedAsync(
        RobloxProcessIdentity process,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// macOS intentionally manages external Roblox windows. Accessibility denial disables only
/// focus/tiling; launching and closing an identity that was verified and registered remain
/// independent operations.
/// </summary>
public sealed partial class MacAccessibilityWindowManager : IClientWindowManager
{
    private readonly IRobloxProcessLocator _processLocator;
    private readonly IMacProcessCommandRunner _commandRunner;

    public MacAccessibilityWindowManager(
        IRobloxProcessLocator? processLocator = null,
        IMacProcessCommandRunner? commandRunner = null)
    {
        _processLocator = processLocator ?? new MacRobloxProcessLocator();
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
    }

    public MacCapabilityResult GetCapability()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return MacCapabilityResult.PlatformNotSupported("Accessibility window management is only available on macOS.");
        }

        return NativeMethods.IsProcessTrusted()
            ? MacCapabilityResult.Supported()
            : MacCapabilityResult.PermissionRequired(
                "Window focus and tiling require Accessibility permission in System Settings > Privacy & Security > Accessibility.");
    }

    public async Task<MacWindowOperationResult> FocusAsync(
        RobloxProcessIdentity process,
        CancellationToken cancellationToken = default)
    {
        var capability = GetCapability();
        if (!capability.IsSupported)
        {
            return new MacWindowOperationResult(false, capability, capability.Message);
        }

        if (!IsCurrentIdentity(process))
        {
            return new MacWindowOperationResult(false, capability, "The process identity is stale or was not verified.");
        }

        var script = BuildVerifiedProcessScript(process, "set frontmost to true");
        var result = await _commandRunner.RunAsync("/usr/bin/osascript", ["-e", script], cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new MacWindowOperationResult(true, capability, null)
            : new MacWindowOperationResult(false, capability, "Accessibility could not focus the Roblox window.");
    }

    public async Task<MacWindowOperationResult> TileAsync(
        IReadOnlyList<RobloxProcessIdentity> processes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var capability = GetCapability();
        if (!capability.IsSupported)
        {
            return new MacWindowOperationResult(false, capability, capability.Message);
        }

        var verified = processes.Where(IsCurrentIdentity).ToList();
        if (verified.Count != processes.Count || verified.Count == 0)
        {
            return new MacWindowOperationResult(false, capability, "One or more Roblox process identities are stale or unverified.");
        }

        var screen = await GetVisibleScreenFrameAsync(cancellationToken).ConfigureAwait(false);
        if (screen is null)
        {
            var appleEventsCapability = new MacCapabilityResult(
                MacCapabilityStatus.PermissionRequired,
                "apple-events-permission-required",
                "Apple Events permission is required to read the current macOS screen frame.");
            return new MacWindowOperationResult(false, appleEventsCapability, appleEventsCapability.Message);
        }

        for (var index = 0; index < verified.Count; index++)
        {
            var layout = MacTileLayout.Default(index, verified.Count, screen.Value.Width, screen.Value.Height);
            layout = layout with { Left = layout.Left + screen.Value.Left, Top = layout.Top + screen.Value.Top };
            // Position and size can be changed without activating the process. Never steal
            // focus from the user's current application while arranging Roblox windows.
            var script = BuildVerifiedProcessScript(verified[index],
                $"if (count windows) > 0 then\nset position of window 1 to {{{layout.Left}, {layout.Top}}}\n"
                + $"set size of window 1 to {{{layout.Width}, {layout.Height}}}\nend if");
            var result = await _commandRunner.RunAsync("/usr/bin/osascript", ["-e", script], cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return new MacWindowOperationResult(false, capability, "Accessibility could not tile a Roblox window.");
            }
        }

        return new MacWindowOperationResult(true, capability, null);
    }

    private static string BuildVerifiedProcessScript(RobloxProcessIdentity process, string body)
    {
        if (string.IsNullOrWhiteSpace(process.BundlePath))
            throw new InvalidOperationException("A verified bundle path is required for Accessibility operations.");
        var path = process.BundlePath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "tell application \"System Events\"\n"
            + $"set candidates to every process whose unix id is {process.ProcessId}\n"
            + "if (count candidates) is not 1 then error \"stale-process-identity\"\n"
            + "set target to item 1 of candidates\n"
            + $"if POSIX path of (application file of target) is not \"{path}\" then error \"stale-process-identity\"\n"
            + "tell target\n"
            + body + "\nend tell\nend tell";
    }

    private async Task<MacScreenFrame?> GetVisibleScreenFrameAsync(CancellationToken cancellationToken)
    {
        // Query the live desktop rather than assuming a fixed 1920x1080 display. If this
        // Apple Events query is denied, no window is moved and the caller receives a clear error.
        var result = await _commandRunner.RunAsync(
            "/usr/bin/osascript",
            ["-e", "tell application \"Finder\" to get bounds of window of desktop"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var values = result.StandardOutput.Trim()
            .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : (int?)null)
            .ToArray();
        if (values.Length < 4 || values.Any(value => value is null))
        {
            return null;
        }

        var left = values[0]!.Value;
        var top = values[1]!.Value;
        var right = values[2]!.Value;
        var bottom = values[3]!.Value;
        return right > left && bottom > top ? new MacScreenFrame(left, top, right - left, bottom - top) : null;
    }

    public async Task<bool> CloseVerifiedAsync(
        RobloxProcessIdentity process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _processLocator.FindProcess(process.ProcessId);
        if (current is null || !_processLocator.IsSameProcess(process, current) || !current.IsManaged)
        {
            return false;
        }

        try
        {
            using var nativeProcess = Process.GetProcessById(process.ProcessId);
            if (nativeProcess.HasExited)
            {
                return true;
            }

            _ = nativeProcess.CloseMainWindow();
            await nativeProcess.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return nativeProcess.HasExited;
        }
        catch (TimeoutException)
        {
            // Do not force-kill after a timeout. The caller can show an explicit close action,
            // but an Accessibility denial or an unexpected PID reuse must never terminate a
            // different process.
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private bool IsCurrentIdentity(RobloxProcessIdentity expected)
    {
        var current = _processLocator.FindProcess(expected.ProcessId);
        return current is not null
            && current.IsManaged
            && _processLocator.IsSameProcess(expected, current);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices",
            EntryPoint = "AXIsProcessTrusted")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool IsProcessTrusted();
    }
}

public readonly record struct MacScreenFrame(int Left, int Top, int Width, int Height);

using System.Text.RegularExpressions;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>
/// Level 2 provides reusable numbered runtime directories. A slot is never removed or rebuilt
/// while any Roblox process is observed beneath it, even when the process is not registered.
/// </summary>
public sealed class MacManagedRuntimeSlotManager
{
    private static readonly Regex SlotName = new("^slot-(?<number>[1-9][0-9]{0,5})$", RegexOptions.CultureInvariant);
    private readonly string _slotsRoot;
    private readonly MacBundleDiscovery _bundleDiscovery;
    private readonly IMacProcessCommandRunner _commandRunner;
    private readonly IRobloxProcessLocator _processLocator;

    public MacManagedRuntimeSlotManager(
        string? runtimeRoot = null,
        MacBundleDiscovery? bundleDiscovery = null,
        IMacProcessCommandRunner? commandRunner = null,
        IRobloxProcessLocator? processLocator = null)
    {
        var root = Path.GetFullPath(runtimeRoot ?? MacManagedRuntimeBuilder.GetDefaultRuntimeRoot());
        _slotsRoot = PathSafety.RequireContainedPath(root, Path.Combine(root, "slots"), allowRoot: true);
        _commandRunner = commandRunner ?? new MacProcessCommandRunner();
        _bundleDiscovery = bundleDiscovery ?? new MacBundleDiscovery(_commandRunner);
        _processLocator = processLocator ?? new MacRobloxProcessLocator();
    }

    public string SlotsRoot => _slotsRoot;

    public async Task<MacSlotAcquireResult> AcquireAsync(
        MacManagedRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PathSafety.EnsureOwnerOnlyDirectory(_slotsRoot);
        var slots = EnumerateSlots();
        var available = slots.FirstOrDefault(slot => !IsBusy(slot.RuntimePath));
        var slotNumber = available?.SlotNumber ?? (slots.Count == 0 ? 1 : slots.Max(slot => slot.SlotNumber) + 1);
        var runtimeName = $"slot-{slotNumber}";
        var builder = new MacManagedRuntimeBuilder(_slotsRoot, _bundleDiscovery, _commandRunner, _processLocator);
        var build = await builder.BuildAsync(request with { RuntimeName = runtimeName }, cancellationToken).ConfigureAwait(false);
        if (!build.Succeeded || build.RuntimePath is null)
        {
            return new MacSlotAcquireResult(false, null, build, build.FailureReason ?? "Unable to build a managed runtime slot.");
        }

        var slot = new MacManagedRuntimeSlot(slotNumber, build.RuntimePath, IsBusy(build.RuntimePath), null);
        if (slot.IsBusy)
        {
            return new MacSlotAcquireResult(false, slot, build, "A process appeared in the slot during acquisition.");
        }

        return new MacSlotAcquireResult(true, slot, build, null);
    }

    public IReadOnlyList<MacManagedRuntimeSlot> EnumerateSlots()
    {
        if (!Directory.Exists(_slotsRoot))
        {
            return Array.Empty<MacManagedRuntimeSlot>();
        }

        PathSafety.RejectSymlinkDirectory(_slotsRoot);
        var slots = new List<MacManagedRuntimeSlot>();
        foreach (var directory in Directory.EnumerateDirectories(_slotsRoot))
        {
            var name = Path.GetFileName(directory);
            var match = SlotName.Match(name);
            if (!match.Success)
            {
                continue;
            }

            PathSafety.RequireContainedPath(_slotsRoot, directory);
            PathSafety.RejectSymlinkDirectory(directory);
            var number = int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var runtime = directory;
            var process = _processLocator.CaptureSnapshot().Processes.FirstOrDefault(
                candidate => PathSafety.IsContainedBy(runtime, candidate.Identity.BundlePath));
            slots.Add(new MacManagedRuntimeSlot(number, runtime, process is not null, process?.Identity));
        }

        return slots.OrderBy(slot => slot.SlotNumber).ToList();
    }

    public IReadOnlyList<string> CleanStaleSlots()
    {
        var removed = new List<string>();
        if (!Directory.Exists(_slotsRoot))
        {
            return removed;
        }

        PathSafety.RejectSymlinkDirectory(_slotsRoot);
        foreach (var directory in Directory.EnumerateDirectories(_slotsRoot))
        {
            var name = Path.GetFileName(directory);
            if (!SlotName.IsMatch(name))
            {
                continue;
            }

            PathSafety.RequireContainedPath(_slotsRoot, directory);
            PathSafety.RejectSymlinkDirectory(directory);
            if (IsBusy(directory))
            {
                continue;
            }

            // Idle does not mean stale: reusable slots are deliberately retained. Cleanup is
            // limited to explicit orphaned/invalid slot directories with no valid runtime stamp.
            var stamp = directory + ".runtime.json";
            if (File.Exists(stamp))
            {
                continue;
            }

            // Recheck containment and symlink state immediately before deletion. A path that
            // changes under us is skipped rather than risking deletion outside the root.
            try
            {
                PathSafety.RejectSymlinkComponents(directory);
                if (!PathSafety.IsContainedBy(_slotsRoot, directory)
                    || PathSafety.PathsEqual(_slotsRoot, directory)
                    || IsBusy(directory))
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Stale cleanup is best-effort and fail-closed.
            }
        }

        return removed;
    }

    private bool IsBusy(string runtimePath)
    {
        try
        {
            return _processLocator.CaptureSnapshot().Processes.Any(
                process => PathSafety.IsContainedBy(runtimePath, process.Identity.BundlePath));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return true;
        }
    }
}

using System.Collections.ObjectModel;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>The safe multi-instance implementation currently selected on macOS.</summary>
public enum MacLaunchLevel
{
    OriginalBundle,
    ManagedRuntime,
    ManagedSlots
}

public enum MacCapabilityStatus
{
    Supported,
    PermissionRequired,
    PlatformNotSupported,
    Unavailable
}

public readonly record struct MacCapabilityResult(
    MacCapabilityStatus Status,
    string Code,
    string Message)
{
    public bool IsSupported => Status == MacCapabilityStatus.Supported;

    public static MacCapabilityResult Supported() =>
        new(MacCapabilityStatus.Supported, "supported", "Supported.");

    public static MacCapabilityResult PlatformNotSupported(string message) =>
        new(MacCapabilityStatus.PlatformNotSupported, "platform-not-supported", message);

    public static MacCapabilityResult PermissionRequired(string message) =>
        new(MacCapabilityStatus.PermissionRequired, "accessibility-permission-required", message);
}

/// <summary>
/// Process identity deliberately includes more than a PID. A PID can be reused immediately
/// after a Roblox process exits, so every operation on a managed client must compare this value.
/// </summary>
public sealed record RobloxProcessIdentity(
    int ProcessId,
    DateTimeOffset StartTime,
    string ExecutablePath,
    string BundlePath)
{
    public bool HasStableStartTime => StartTime != default;

    public bool Matches(RobloxProcessIdentity other)
    {
        return ProcessId == other.ProcessId
            && StartTime == other.StartTime
            && PathSafety.PathsEqual(ExecutablePath, other.ExecutablePath)
            && PathSafety.PathsEqual(BundlePath, other.BundlePath);
    }
}

public sealed record RobloxProcessInfo(
    RobloxProcessIdentity Identity,
    string ProcessName,
    bool IsManaged,
    bool IsStable)
{
    public int ProcessId => Identity.ProcessId;
}

public sealed class RobloxLaunchSnapshot
{
    public RobloxLaunchSnapshot(DateTimeOffset capturedAt, IEnumerable<RobloxProcessInfo> processes)
    {
        CapturedAt = capturedAt;
        Processes = new ReadOnlyCollection<RobloxProcessInfo>(processes.ToList());
    }

    public DateTimeOffset CapturedAt { get; }

    public IReadOnlyList<RobloxProcessInfo> Processes { get; }

    public bool Contains(RobloxProcessIdentity identity) =>
        Processes.Any(process => process.Identity.Matches(identity));
}

public sealed record RobloxLaunchRequest(
    string BundlePath,
    Func<CancellationToken, ValueTask<Uri>> FreshLaunchUri,
    bool UserConsentedToManagedCopy,
    TimeSpan? VerificationTimeout = null);

public enum SingletonReleaseStatus
{
    Removed,
    AlreadyAbsent,
    Failed,
    NotMacOS
}

public sealed record SingletonReleaseResult(
    SingletonReleaseStatus Status,
    int NativeError,
    string? ErrorName)
{
    public bool Succeeded => Status is SingletonReleaseStatus.Removed or SingletonReleaseStatus.AlreadyAbsent;
}

public enum LaunchVerificationStatus
{
    Verified,
    TimedOut,
    ExistingProcessOnly,
    InvalidBundle,
    Failed
}

public sealed record LaunchVerificationResult(
    LaunchVerificationStatus Status,
    RobloxProcessInfo? NewProcess,
    bool PriorManagedProcessDisappeared,
    IReadOnlyList<string> Warnings)
{
    public bool Succeeded => Status == LaunchVerificationStatus.Verified;
}

public sealed record MacLaunchResult(
    MacLaunchLevel Level,
    SingletonReleaseResult BeforeLaunchSemaphore,
    SingletonReleaseResult AfterLaunchSemaphore,
    LaunchVerificationResult Verification,
    bool LaunchCommandSucceeded,
    IReadOnlyList<string> Warnings);

public sealed record MacProcessCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public override string ToString() =>
        $"ExitCode={ExitCode}; Output is intentionally omitted from diagnostics.";
}

public sealed record MacBundleInfo(
    string BundlePath,
    string BundleIdentifier,
    string ExecutablePath,
    string? Version,
    string? Build,
    bool SignatureVerified,
    string SourceFingerprint,
    string ExecutableFingerprint,
    string PlistFingerprint);

public sealed record MacManagedRuntimeRequest(
    string SourceBundlePath,
    string RuntimeName,
    bool ForceRebuild = false,
    MacLaunchLevel Level = MacLaunchLevel.ManagedRuntime);

public enum MacRuntimeBuildStatus
{
    Built,
    Reused,
    Busy,
    InvalidSource,
    Failed
}

public sealed record MacManagedRuntimeBuildResult(
    MacRuntimeBuildStatus Status,
    string? RuntimePath,
    MacBundleInfo? Source,
    string? SourceFingerprint,
    string? FailureReason)
{
    public bool Succeeded => Status is MacRuntimeBuildStatus.Built or MacRuntimeBuildStatus.Reused;
}

public sealed record MacRuntimeStamp(
    string SourcePath,
    string SourceFingerprint,
    string BundleVersion,
    string BundleBuild,
    string ExecutableFingerprint,
    string PlistFingerprint,
    DateTimeOffset BuiltAt,
    MacLaunchLevel Level,
    int BuilderRevision = 0);

public sealed record MacManagedRuntimeSlot(
    int SlotNumber,
    string RuntimePath,
    bool IsBusy,
    RobloxProcessIdentity? Process);

public sealed record MacSlotAcquireResult(
    bool Succeeded,
    MacManagedRuntimeSlot? Slot,
    MacManagedRuntimeBuildResult Build,
    string? FailureReason,
    IAsyncDisposable? Lease = null);

public sealed record MacTileLayout(int Left, int Top, int Width, int Height)
{
    public static MacTileLayout Default(int index, int total, int screenWidth = 1920, int screenHeight = 1080)
    {
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, total))));
        var rows = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, total) / columns));
        var width = Math.Max(320, screenWidth / columns);
        var height = Math.Max(240, screenHeight / rows);
        return new(index % columns * width, index / columns * height, width, height);
    }
}

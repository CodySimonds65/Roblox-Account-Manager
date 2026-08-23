using System.Text.Json.Serialization;

namespace RobloxAccountManager.Core.Contracts;

/// <summary>Operating-system family used by platform capability and plugin selection.</summary>
public enum RobloxPlatform
{
    Unknown = 0,
    Windows = 1,
    MacOS = 2
}

public enum MacLaunchLevel
{
    OriginalBundle = 0,
    ManagedRuntime = 1,
    ManagedSlots = 2
}

public enum SingletonReleaseStatus
{
    Released = 0,
    AlreadyAbsent = 1,
    PermissionDenied = 2,
    Failed = 3,
    NotSupported = 4
}

public enum LaunchFailureKind
{
    None = 0,
    PlatformNotSupported = 1,
    LauncherRejected = 2,
    ProcessNotFound = 3,
    ProcessExitedEarly = 4,
    VerificationFailed = 5,
    Cancelled = 6,
    RetryLimitReached = 7
}

public enum CapabilityStatus
{
    Supported = 0,
    Unsupported = 1,
    RequiresPermission = 2,
    Disabled = 3
}

/// <summary>
/// Identity is deliberately stronger than a PID. A PID can be recycled while a
/// launch is in flight, so all process assignment must retain the start identity
/// and executable/bundle paths.
/// </summary>
public sealed record RobloxProcessIdentity(
    int Pid,
    DateTimeOffset StartTimeUtc,
    string ExecutablePath,
    string? BundlePath,
    RobloxPlatform Platform = RobloxPlatform.Unknown)
{
    public bool IsValid => Pid > 0 && StartTimeUtc != default && !string.IsNullOrWhiteSpace(ExecutablePath);

    public bool Matches(RobloxProcessIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var isMac = Platform == RobloxPlatform.MacOS || other.Platform == RobloxPlatform.MacOS;
        var comparison = isMac ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Pid == other.Pid &&
            StartTimeUtc == other.StartTimeUtc &&
            string.Equals(CanonicalPath(ExecutablePath, isMac), CanonicalPath(other.ExecutablePath, isMac), comparison) &&
            string.Equals(CanonicalPath(BundlePath, isMac), CanonicalPath(other.BundlePath, isMac), comparison);
    }

    private static string? CanonicalPath(string? path, bool isMac)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var canonical = path.Trim();
        if (!isMac)
            canonical = Path.GetFullPath(canonical);
        return canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed record RobloxProcessInfo(
    RobloxProcessIdentity Identity,
    bool IsManaged,
    string? AccountId = null,
    string? RuntimePath = null);

public sealed record RobloxLaunchSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<RobloxProcessIdentity> Processes)
{
    public static RobloxLaunchSnapshot Empty => new(DateTimeOffset.UtcNow, Array.Empty<RobloxProcessIdentity>());

    public bool Contains(RobloxProcessIdentity identity) => Processes.Any(existing => existing.Matches(identity));
}

/// <summary>
/// A request owns a fresh URI factory. The factory is called for every attempt;
/// captured authentication-ticket URIs are never stored in a retry record.
/// </summary>
public sealed record RobloxLaunchRequest(
    string AccountId,
    Func<CancellationToken, ValueTask<Uri>> FreshUriFactory,
    int MaxAttempts = 3,
    MacLaunchLevel? PreferredMacLevel = null,
    string? RobloxBundlePath = null,
    TimeSpan? VerificationTimeout = null,
    // Populated by the platform launcher after it validates the bundle and is
    // carried into process verification so a post-launch path replacement
    // cannot establish a new trust baseline.
    string? ValidatedRobloxBundleFingerprint = null);

/// <summary>
/// The platform may replace the source bundle with a prepared runtime before a
/// one-use launch URI is requested. Resources held by the preparation remain
/// leased until every attempt in the serialized launch operation has finished.
/// </summary>
public sealed record RobloxLaunchPreparation(
    bool Succeeded,
    RobloxLaunchRequest Request,
    LaunchFailureKind FailureKind = LaunchFailureKind.None,
    string? DiagnosticCode = null,
    MacLaunchLevel? ActiveMacLevel = null,
    IAsyncDisposable? Lease = null)
{
    public static RobloxLaunchPreparation Success(
        RobloxLaunchRequest request,
        MacLaunchLevel? activeMacLevel = null,
        IAsyncDisposable? lease = null) =>
        new(true, request, ActiveMacLevel: activeMacLevel, Lease: lease);

    public static RobloxLaunchPreparation Failure(
        RobloxLaunchRequest request,
        LaunchFailureKind failureKind,
        string diagnosticCode) =>
        new(false, request, failureKind, diagnosticCode);
}

public sealed record SingletonReleaseResult(
    SingletonReleaseStatus Status,
    int? NativeError = null,
    string? DiagnosticCode = null)
{
    public bool Succeeded => Status is SingletonReleaseStatus.Released or SingletonReleaseStatus.AlreadyAbsent;
}

public sealed record LaunchVerificationResult(
    bool Succeeded,
    RobloxProcessInfo? Process,
    LaunchFailureKind FailureKind = LaunchFailureKind.None,
    string? DiagnosticCode = null,
    bool PriorManagedProcessesDisappeared = false)
{
    public static LaunchVerificationResult Failure(LaunchFailureKind kind, string code) =>
        new(false, null, kind, code);
}

public sealed record PlatformLaunchResult(
    bool Accepted,
    LaunchFailureKind FailureKind = LaunchFailureKind.None,
    string? DiagnosticCode = null,
    string? ValidatedRobloxBundleFingerprint = null)
{
    public static PlatformLaunchResult Success(string? validatedRobloxBundleFingerprint = null) =>
        new(true, ValidatedRobloxBundleFingerprint: validatedRobloxBundleFingerprint);
}

public sealed record LaunchAttemptDiagnostic(
    int Attempt,
    LaunchFailureKind Outcome,
    string DiagnosticCode,
    int? Pid = null,
    DateTimeOffset? ProcessStartTimeUtc = null,
    string? BundlePath = null,
    SingletonReleaseStatus? SingletonStatus = null,
    int? NativeError = null);

public sealed record LaunchResult(
    bool Succeeded,
    RobloxProcessInfo? Process,
    IReadOnlyList<LaunchAttemptDiagnostic> Attempts,
    LaunchFailureKind FailureKind = LaunchFailureKind.None)
{
    public static LaunchResult Cancelled(IReadOnlyList<LaunchAttemptDiagnostic> attempts) =>
        new(false, null, attempts, LaunchFailureKind.Cancelled);
}

public sealed record BrowserSessionDescriptor(
    string AccountId,
    string ProfileName,
    string DataStoreIdentifier,
    RobloxPlatform Platform);

public sealed record BrowserNavigationResult
{
    private Uri? _launchUri;

    public BrowserNavigationResult(bool accepted, Uri? launchUri = null, string? diagnosticCode = null)
    {
        Accepted = accepted;
        _launchUri = launchUri;
        DiagnosticCode = diagnosticCode;
    }

    public bool Accepted { get; }

    public string? DiagnosticCode { get; }

    public bool TryConsumeLaunchUri(out Uri? launchUri)
    {
        launchUri = Interlocked.Exchange(ref _launchUri, null);
        return launchUri is not null;
    }

    public static BrowserNavigationResult Rejected(string code) => new(false, null, code);

    public override string ToString() =>
        $"BrowserNavigationResult {{ Accepted = {Accepted}, DiagnosticCode = {DiagnosticCode ?? "none"} }}";
}

public sealed record RobloxWindowInfo(
    RobloxProcessIdentity Process,
    string? WindowIdentifier,
    string? Title,
    int? LegacyWindowHandle = null,
    string? AccountId = null);

public sealed record CapabilityDescriptor(
    string Name,
    CapabilityStatus Status,
    string Description,
    string? StableFailureCode = null);

public sealed record PlatformCapabilitySnapshot(
    RobloxPlatform Platform,
    IReadOnlyList<CapabilityDescriptor> Capabilities)
{
    public CapabilityDescriptor this[string name] =>
        Capabilities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? new CapabilityDescriptor(name, CapabilityStatus.Unsupported, "This capability was not registered.", "platform-not-supported");
}

public sealed record PluginRidEntryPoint(string Rid, string EntryPoint);

public sealed record PluginManifest(
    string Id,
    string Name,
    int SchemaVersion,
    string? LegacyEntryPoint,
    IReadOnlyList<PluginRidEntryPoint> EntryPoints,
    IReadOnlySet<string> RequestedCapabilities);

public sealed record PluginAccountSnapshot(
    string AccountId,
    string DisplayName,
    RobloxPlatform? Platform = null,
    string? WindowIdentifier = null,
    int? LegacyWindowHandle = null);

public sealed record PluginConnectionRequest(
    string PluginId,
    string TransportEndpoint,
    [property: JsonIgnore] string AuthenticationToken,
    TimeSpan Timeout)
{
    public override string ToString() =>
        $"PluginConnectionRequest {{ PluginId = {PluginId}, TransportEndpoint = {TransportEndpoint}, Timeout = {Timeout} }}";
}

public sealed record PluginCapabilityResult(
    string Capability,
    CapabilityStatus Status,
    string? StableFailureCode = null);

public sealed record PluginInstallResult(
    bool Succeeded,
    string? PluginId,
    string DiagnosticCode)
{
    public static PluginInstallResult Rejected(string code) => new(false, null, code);
    public static PluginInstallResult Success(string pluginId) => new(true, pluginId, "installed");
}

public sealed record PluginLifecycleResult(
    bool Succeeded,
    string DiagnosticCode)
{
    public static PluginLifecycleResult Success() => new(true, "running");
    public static PluginLifecycleResult Rejected(string code) => new(false, code);
}

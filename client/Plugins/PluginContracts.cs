using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxAltClient.Plugins;

public static class PluginProtocol
{
    public const int CurrentMajor = 1;
    public const int CurrentMinor = 0;
    public const int MaxMessageBytes = 1 * 1024 * 1024;
    public const string PipePrefix = "RobloxAccountManager.PluginHost.v1.";
}

public static class PluginCapabilities
{
    public const string HostAccountsRead = "host.accounts.read";
    public const string HostAccountEvents = "host.events.account-lifecycle";
    public const string HostActivityRead = "host.queries.account-activity";
    public const string HostThemeRead = "host.theme.read";
    public const string HostInputBackground = "host.input.background";
    public const string HostActionsRegister = "host.actions.register";
    public const string HostActionsInvoke = "host.actions.invoke";
    public const string SystemWatchGlobalInput = "system.watch-global-input";
    public const string SystemReadScreen = "system.read-screen";
}

public sealed record ManagedAccountSnapshot(
    string AccountId,
    string Label,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    nint WindowHandle,
    int ClientX,
    int ClientY,
    int ClientWidth,
    int ClientHeight,
    uint Dpi,
    bool IsMinimized,
    DateTime LastActivityUtc,
    bool IsRunning);

public sealed record ThemePalette(
    string Background,
    string Surface,
    string Elevated,
    string Hover,
    string Border,
    string Text,
    string MutedText,
    string Accent,
    string AccentHover,
    string AccentPressed,
    string Danger,
    string Success,
    string Input,
    string SelectionSurface,
    string SelectionBorder);

public enum PluginInputKind
{
    KeyDown,
    KeyUp,
    MouseMove,
    MouseButtonDown,
    MouseButtonUp,
    MouseWheel
}

public sealed record PluginInputEvent(
    PluginInputKind Kind,
    int VirtualKey,
    int ScanCode,
    bool Extended,
    int Button,
    int WheelDelta,
    double NormalizedX,
    double NormalizedY,
    long OffsetMicroseconds);

public sealed record ActionDescriptor(
    string ActionId,
    string DisplayName,
    string Description,
    string ArgumentSchemaJson,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record ActionInvocation(
    string ActionId,
    string RequestId,
    IReadOnlyList<string> AccountIds,
    JsonElement Arguments,
    DateTime RequestedUtc);

public sealed record ActionResult(bool Accepted, string Code, string Message, JsonElement? Data = null)
{
    public static ActionResult Ok(string message = "Accepted") => new(true, "ok", message);
    public static ActionResult Fail(string code, string message) => new(false, code, message);
}

public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Version,
    string ContractVersion,
    string Publisher,
    string Description,
    IReadOnlyList<string> Capabilities,
    string EntryPoint,
    string? Icon = null,
    string? UpdateFeed = null,
    string? MinHostVersion = null,
    bool AutostartDefault = false);

public sealed record InstalledPlugin(
    PluginManifest Manifest,
    string InstallDirectory,
    bool Autostart,
    IReadOnlySet<string> GrantedCapabilities,
    bool IsRunning,
    int? ProcessId,
    string? LastError)
{
    public string EntryPointPath => Path.Combine(InstallDirectory, Manifest.EntryPoint);
}

public sealed record PluginEnvelope(
    string Type,
    string RequestId,
    JsonElement Payload,
    int ProtocolMajor = PluginProtocol.CurrentMajor,
    int ProtocolMinor = PluginProtocol.CurrentMinor);

public sealed record PluginHandshake(
    string PluginId,
    string Token,
    int ProtocolMajor,
    int ProtocolMinor,
    string ManifestSha256,
    IReadOnlyList<string> DeclaredCapabilities,
    int ProcessId = 0);

public sealed class PluginJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

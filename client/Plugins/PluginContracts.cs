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
    // Retained so already-installed manifests can receive an explicit
    // foreground-required failure instead of silently falling back to a
    // delivery mechanism Roblox does not consume.
    public const string HostInputBackgroundMessages = "host.input.background.messages";
    public const string HostInputForegroundReal = "host.input.foreground.real";
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
    bool IsRunning,
    nint RootWindowHandle = 0,
    int? ExitCode = null);

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

/// <summary>Legacy delivery preference retained for wire compatibility.</summary>
public enum InputDeliveryIntent
{
    Default,
    PostMessageProbe
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

public sealed record ForegroundSessionRequest(
    string[] AccountIds,
    string Purpose = "automation",
    bool RestoreForeground = true);

public sealed record ForegroundSessionAccountRequest(string SessionId, string AccountId);

public sealed record ForegroundSessionCloseRequest(
    string SessionId,
    bool RestoreForeground = true,
    bool UserInitiated = false);

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
    int ProcessId = 0,
    long ProcessStartTimeUtcTicks = 0);

public sealed class PluginJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new NativeIntJsonConverter(), new ManagedAccountSnapshotJsonConverter(), new BackgroundInputResultJsonConverter() }
    };
}

internal sealed class NativeIntJsonConverter : JsonConverter<IntPtr>
{
    public override IntPtr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, IntPtr value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.ToInt64());
}

internal sealed class ManagedAccountSnapshotJsonConverter : JsonConverter<ManagedAccountSnapshot>
{
    public override ManagedAccountSnapshot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        return new ManagedAccountSnapshot(
            value.GetProperty("accountId").GetString() ?? string.Empty,
            value.GetProperty("label").GetString() ?? string.Empty,
            value.GetProperty("processId").GetInt32(),
            value.GetProperty("processStartTimeUtcTicks").GetInt64(),
            (nint)value.GetProperty("windowHandle").GetInt64(),
            value.GetProperty("clientX").GetInt32(),
            value.GetProperty("clientY").GetInt32(),
            value.GetProperty("clientWidth").GetInt32(),
            value.GetProperty("clientHeight").GetInt32(),
            value.GetProperty("dpi").GetUInt32(),
            value.GetProperty("isMinimized").GetBoolean(),
            value.GetProperty("lastActivityUtc").GetDateTime(),
            value.GetProperty("isRunning").GetBoolean(),
            value.TryGetProperty("rootWindowHandle", out var root) ? (nint)root.GetInt64() : nint.Zero,
            value.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind != JsonValueKind.Null ? exitCode.GetInt32() : null);
    }

    public override void Write(Utf8JsonWriter writer, ManagedAccountSnapshot value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("accountId", value.AccountId);
        writer.WriteString("label", value.Label);
        writer.WriteNumber("processId", value.ProcessId);
        writer.WriteNumber("processStartTimeUtcTicks", value.ProcessStartTimeUtcTicks);
        writer.WriteNumber("windowHandle", value.WindowHandle.ToInt64());
        writer.WriteNumber("clientX", value.ClientX);
        writer.WriteNumber("clientY", value.ClientY);
        writer.WriteNumber("clientWidth", value.ClientWidth);
        writer.WriteNumber("clientHeight", value.ClientHeight);
        writer.WriteNumber("dpi", value.Dpi);
        writer.WriteBoolean("isMinimized", value.IsMinimized);
        writer.WriteString("lastActivityUtc", value.LastActivityUtc);
        writer.WriteBoolean("isRunning", value.IsRunning);
        writer.WriteNumber("rootWindowHandle", value.RootWindowHandle.ToInt64());
        if (value.ExitCode is int exitCode)
        {
            writer.WriteNumber("exitCode", exitCode);
        }
        writer.WriteEndObject();
    }
}

internal sealed class BackgroundInputResultJsonConverter : JsonConverter<BackgroundInputResult>
{
    public override BackgroundInputResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        return new BackgroundInputResult(
            value.GetProperty("accepted").GetBoolean(),
            value.GetProperty("code").GetString() ?? string.Empty,
            value.GetProperty("message").GetString() ?? string.Empty,
            value.GetProperty("postedCount").GetInt32(),
            (nint)value.GetProperty("foregroundBefore").GetInt64(),
            (nint)value.GetProperty("foregroundAfter").GetInt64())
        {
            DeliveryMode = value.TryGetProperty("deliveryMode", out var mode) ? mode.GetString() ?? "unknown" : "unknown",
            Verification = value.TryGetProperty("verification", out var verification) ? verification.GetString() ?? "unverified" : "unverified",
            TraceId = value.TryGetProperty("traceId", out var trace) && trace.ValueKind != JsonValueKind.Null ? trace.GetString() : null,
            RequestedCount = value.TryGetProperty("requestedCount", out var requested) ? requested.GetInt32() : 0,
            TargetRootWindow = value.TryGetProperty("targetRootWindow", out var root) ? (nint)root.GetInt64() : nint.Zero,
            TargetRenderWindow = value.TryGetProperty("targetRenderWindow", out var render) ? (nint)render.GetInt64() : nint.Zero,
            TargetProcessId = value.TryGetProperty("targetProcessId", out var pid) ? pid.GetInt32() : 0,
            TargetProcessStartTimeUtcTicks = value.TryGetProperty("targetProcessStartTimeUtcTicks", out var start) ? start.GetInt64() : 0,
            CursorX = value.TryGetProperty("cursorX", out var cursorX) ? cursorX.GetInt32() : 0,
            CursorY = value.TryGetProperty("cursorY", out var cursorY) ? cursorY.GetInt32() : 0,
            SelectedAccountId = value.TryGetProperty("selectedAccountId", out var selected) && selected.ValueKind != JsonValueKind.Null ? selected.GetString() : null,
            SelectedVisible = value.TryGetProperty("selectedVisible", out var visible) && visible.ValueKind != JsonValueKind.Null ? visible.GetBoolean() : null
        };
    }

    public override void Write(Utf8JsonWriter writer, BackgroundInputResult value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("accepted", value.Accepted);
        writer.WriteString("code", value.Code);
        writer.WriteString("message", value.Message);
        writer.WriteNumber("postedCount", value.PostedCount);
        writer.WriteNumber("foregroundBefore", value.ForegroundBefore.ToInt64());
        writer.WriteNumber("foregroundAfter", value.ForegroundAfter.ToInt64());
        writer.WriteString("deliveryMode", value.DeliveryMode);
        writer.WriteString("verification", value.Verification);
        if (value.TraceId is not null) writer.WriteString("traceId", value.TraceId);
        writer.WriteNumber("requestedCount", value.RequestedCount);
        writer.WriteNumber("targetRootWindow", value.TargetRootWindow.ToInt64());
        writer.WriteNumber("targetRenderWindow", value.TargetRenderWindow.ToInt64());
        writer.WriteNumber("targetProcessId", value.TargetProcessId);
        writer.WriteNumber("targetProcessStartTimeUtcTicks", value.TargetProcessStartTimeUtcTicks);
        writer.WriteNumber("cursorX", value.CursorX);
        writer.WriteNumber("cursorY", value.CursorY);
        if (value.SelectedAccountId is not null) writer.WriteString("selectedAccountId", value.SelectedAccountId);
        if (value.SelectedVisible is not null) writer.WriteBoolean("selectedVisible", value.SelectedVisible.Value);
        writer.WriteEndObject();
    }
}

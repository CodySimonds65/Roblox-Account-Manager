using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.PluginSdk;

public static class PluginJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new NativeIntJsonConverter(), new ManagedAccountSnapshotJsonConverter() }
    };
}

/// <summary>Serializes HWND-sized values as JSON numbers for cross-process, cross-bitness wire compatibility.</summary>
public sealed class NativeIntJsonConverter : JsonConverter<IntPtr>
{
    public override IntPtr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, IntPtr value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.ToInt64());
}

public sealed class ManagedAccountSnapshotJsonConverter : JsonConverter<ManagedAccountSnapshot>
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
            value.TryGetProperty("platform", out var platform) && platform.ValueKind == JsonValueKind.String ? platform.GetString() : null,
            value.TryGetProperty("windowIdentifier", out var windowIdentifier) && windowIdentifier.ValueKind == JsonValueKind.String
                ? windowIdentifier.GetString()
                : null);
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
        if (value.Platform is not null) writer.WriteString("platform", value.Platform);
        if (value.WindowIdentifier is not null) writer.WriteString("windowIdentifier", value.WindowIdentifier);
        writer.WriteEndObject();
    }
}

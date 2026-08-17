using System.Text.Json;

namespace RobloxAccountManager.PluginSdk;

public static class PluginProtocol
{
    public const int CurrentMajor = 1;
    public const int CurrentMinor = 0;
    public const int MaxMessageBytes = 1 * 1024 * 1024;
}

public sealed record ManagedAccountSnapshot(string AccountId, string Label, int ProcessId, long ProcessStartTimeUtcTicks,
    nint WindowHandle, int ClientX, int ClientY, int ClientWidth, int ClientHeight, uint Dpi,
    bool IsMinimized, DateTime LastActivityUtc, bool IsRunning);

public sealed record ThemePalette(string Background, string Surface, string Elevated, string Hover, string Border,
    string Text, string MutedText, string Accent, string AccentHover, string AccentPressed, string Danger,
    string Success, string Input, string SelectionSurface, string SelectionBorder);

public enum InputEventKind { KeyDown, KeyUp, MouseMove, MouseButtonDown, MouseButtonUp, MouseWheel }
public sealed record InputEvent(InputEventKind Kind, int VirtualKey, int ScanCode, bool Extended, int Button,
    int WheelDelta, double NormalizedX, double NormalizedY, long OffsetMicroseconds);

public sealed record ActionDescriptor(string ActionId, string DisplayName, string Description,
    string ArgumentSchemaJson, IReadOnlyList<string> RequiredCapabilities);

public sealed record ActionInvocation(string ActionId, string RequestId, IReadOnlyList<string> AccountIds,
    JsonElement Arguments, DateTime RequestedUtc);

public sealed record ActionResult(bool Accepted, string Code, string Message, JsonElement? Data = null);

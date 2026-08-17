using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.PluginSdk;

public static class PluginJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

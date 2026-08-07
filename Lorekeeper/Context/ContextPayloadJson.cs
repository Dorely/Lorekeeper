using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Context;

internal static class ContextPayloadJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
        WriteIndented = false,
    };
}

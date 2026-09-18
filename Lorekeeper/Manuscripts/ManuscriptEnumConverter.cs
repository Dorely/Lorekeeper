using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Manuscripts;

/// <summary>Keep attributed manuscript enums identical on persistence and authoring wires.</summary>
public sealed class ManuscriptEnumConverter<TEnum>()
    : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

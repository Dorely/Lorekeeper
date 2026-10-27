using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lorekeeper.Persistence;

/// <summary>Opaque exact-state preconditions for authored owners without numeric revisions.</summary>
public static class ContentVersion
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new UtcDateTimeConverter() } };
    public static string For<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        Canonical(JsonSerializer.SerializeToElement(value, Json)))));

    public static void Require<T>(string? expectedVersion, T current)
    {
        if (expectedVersion is not null && expectedVersion != For(current))
            throw new ContentVersionConflictException(For(current));
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item.Name) + ":" + Canonical(item.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };

    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTime();
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime());
    }
}

public sealed class ContentVersionConflictException(string currentVersion)
    : InvalidOperationException("The authored state changed. Reread the target and reassess the edit; the newer state has been preserved.")
{
    public string CurrentVersion { get; } = currentVersion;
}

public sealed class ContentRevisionConflictException(long currentRevision)
    : InvalidOperationException("The authored revision changed. Read the current target before saving; newer content has been preserved.")
{
    public long CurrentRevision { get; } = currentRevision;
}

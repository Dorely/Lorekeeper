using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lorekeeper.VersionHistory.Snapshots;

internal static class VersionHistoryCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static byte[] Serialize(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, SerializerOptions)
            ?? throw new InvalidDataException("Snapshot value serialized to null.");
        var canonical = Canonicalize(node);
        return Encoding.UTF8.GetBytes(canonical.ToJsonString(SerializerOptions));
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var node = JsonNode.Parse(bytes)
                ?? throw new InvalidDataException("Snapshot JSON value is null.");
            var canonical = Canonicalize(node);
            return JsonSerializer.Deserialize<T>(canonical.ToJsonString(SerializerOptions), SerializerOptions)
                ?? throw new InvalidDataException("Snapshot JSON value is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Snapshot JSON is malformed.", exception);
        }
    }

    /// <summary>
    /// Canonicalizes a manuscript document for its direct snapshot file. This
    /// uses the same recursive embedded-JSON and sensitive-property checks as
    /// JSON values embedded in the other snapshot DTOs, but returns an object
    /// document rather than a JSON-escaped string.
    /// </summary>
    public static byte[] SerializeDirectManuscript(string manuscriptJson)
    {
        if (string.IsNullOrWhiteSpace(manuscriptJson))
            throw new InvalidDataException("Snapshot chapter manuscript JSON is empty.");

        JsonNode node;
        try
        {
            node = JsonNode.Parse(manuscriptJson)
                ?? throw new InvalidDataException("Snapshot chapter manuscript JSON is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Snapshot chapter manuscript JSON is malformed.", exception);
        }

        return SerializeDirectManuscript(node);
    }

    public static string DeserializeDirectManuscript(ReadOnlySpan<byte> bytes)
    {
        JsonNode node;
        try
        {
            node = JsonNode.Parse(bytes)
                ?? throw new InvalidDataException("Snapshot chapter manuscript JSON is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Snapshot chapter manuscript JSON is malformed.", exception);
        }

        var canonicalBytes = SerializeDirectManuscript(node);
        if (!bytes.SequenceEqual(canonicalBytes))
            throw new InvalidDataException("Snapshot chapter manuscript JSON is not in canonical schema-v1 form.");

        return Encoding.UTF8.GetString(canonicalBytes);
    }

    public static JsonNode Canonicalize(JsonNode node)
    {
        return CanonicalizeNode(node, scanEmbeddedProperties: false);
    }

    private static JsonNode CanonicalizeNode(JsonNode node, bool scanEmbeddedProperties)
    {
        return node switch
        {
            JsonObject obj => CanonicalizeObject(obj, scanEmbeddedProperties),
            JsonArray array => CanonicalizeArray(array, scanEmbeddedProperties),
            JsonValue value => value.DeepClone(),
            _ => throw new InvalidDataException("Snapshot JSON contains an unsupported node."),
        };
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var pathBytes = Encoding.UTF8.GetBytes(path);
            var lengthBytes = Encoding.UTF8.GetBytes(bytes.LongLength.ToString(CultureInfo.InvariantCulture));
            hash.AppendData(pathBytes);
            hash.AppendData([0]);
            hash.AppendData(lengthBytes);
            hash.AppendData([0]);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool IsOperationalProperty(string propertyName) =>
        propertyName.Equals("exportedAtUtc", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("warnings", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("diagnostic", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("errorMessage", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("createdAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("updatedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("startedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("completedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("heartbeatAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("resolvedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("lastValidatedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("revokedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("fetchedAt", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("providerId", StringComparison.OrdinalIgnoreCase)
        || propertyName.EndsWith("ProviderId", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("rawProviderResponseJson", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("progressPercent", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("progressMessage", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("currentMessage", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static byte[] SerializeDirectManuscript(JsonNode node)
    {
        if (node is not JsonObject)
            throw new InvalidDataException("Snapshot chapter manuscript JSON must be an object.");

        var canonical = CanonicalizeNode(node, scanEmbeddedProperties: true);
        return Encoding.UTF8.GetBytes(canonical.ToJsonString(SerializerOptions));
    }

    private static bool IsSensitiveEmbeddedProperty(string propertyName)
    {
        var normalized = string.Concat(propertyName.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized is "token" or "accesstoken" or "refreshtoken" or "bearertoken" or "idtoken"
            || normalized.EndsWith("accesstoken", StringComparison.Ordinal)
            || normalized.EndsWith("refreshtoken", StringComparison.Ordinal)
            || normalized.EndsWith("bearertoken", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("sessionid", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("authcode", StringComparison.Ordinal);
    }

    private static JsonObject CanonicalizeObject(JsonObject source, bool scanEmbeddedProperties)
    {
        var result = new JsonObject();
        foreach (var property in source.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (scanEmbeddedProperties && IsSensitiveEmbeddedProperty(property.Key))
                throw new InvalidDataException($"Embedded JSON property '{property.Key}' is not permitted in a snapshot.");

            if (IsOperationalProperty(property.Key))
                continue;

            if (property.Value is null)
            {
                result[property.Key] = null;
                continue;
            }

            if (property.Value is JsonValue value
                && property.Key.EndsWith("Json", StringComparison.OrdinalIgnoreCase)
                && value.TryGetValue<string>(out var embeddedText)
                && !string.IsNullOrWhiteSpace(embeddedText))
            {
                JsonNode embedded;
                try
                {
                    embedded = JsonNode.Parse(embeddedText)!;
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        $"Embedded JSON property '{property.Key}' is malformed.",
                        exception);
                }

                if (embedded is null)
                {
                    if (string.Equals(embeddedText.Trim(), "null", StringComparison.Ordinal))
                    {
                        result[property.Key] = "null";
                        continue;
                    }

                    throw new InvalidDataException($"Embedded JSON property '{property.Key}' is malformed.");
                }

                var canonicalEmbedded = CanonicalizeNode(embedded, scanEmbeddedProperties: true);
                result[property.Key] = canonicalEmbedded.ToJsonString(SerializerOptions);
                continue;
            }

            result[property.Key] = CanonicalizeNode(property.Value, scanEmbeddedProperties);
        }

        return result;
    }

    private static JsonArray CanonicalizeArray(JsonArray source, bool scanEmbeddedProperties)
    {
        var result = new JsonArray();
        foreach (var value in source)
            result.Add(value is null ? null : CanonicalizeNode(value, scanEmbeddedProperties));
        return result;
    }

    private static JsonSerializerOptions CreateOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };
}

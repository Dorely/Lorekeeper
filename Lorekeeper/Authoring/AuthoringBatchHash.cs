using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Manuscripts;

namespace Lorekeeper.Authoring;

public static class AuthoringBatchHash
{
    public static string Compute(AuthoringBatchV1 batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(batch, ManuscriptCodec.JsonOptions));
        return Compute(document.RootElement);
    }

    // Hashes a client request exactly as sent. Re-serializing it through the model would add
    // defaults the editor omits (for example "decorative": false on every block).
    public static string Compute(JsonElement request)
    {
        var canonical = new StringBuilder();
        WriteCanonical(canonical, request, isRoot: true);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    // Must stay byte-identical to the editor's canonicalJson, which escapes strings with JSON.stringify.
    private static void WriteCanonical(StringBuilder output, JsonElement element, bool isRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject()
                             .Where(property => property.Value.ValueKind != JsonValueKind.Null
                                 && !(isRoot && property.NameEquals("requestHash")))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(output, property.Name);
                    output.Append(':');
                    WriteCanonical(output, property.Value);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) output.Append(',');
                    firstItem = false;
                    WriteCanonical(output, item);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(output, element.GetString()!);
                break;
            case JsonValueKind.Number:
                output.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                output.Append("null");
                break;
            default:
                throw new InvalidDataException("The authoring batch contains an unsupported JSON value.");
        }
    }

    private static void WriteString(StringBuilder output, string value)
    {
        output.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < ' ')
                    {
                        output.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    {
                        output.Append(character).Append(value[++index]);
                    }
                    else if (char.IsSurrogate(character))
                    {
                        output.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        output.Append(character);
                    }
                    break;
            }
        }
        output.Append('"');
    }
}

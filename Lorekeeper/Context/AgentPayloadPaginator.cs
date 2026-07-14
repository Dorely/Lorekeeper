using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lorekeeper.Context;

/// <summary>
/// Produces model-facing pages without cutting serialized JSON at an arbitrary character offset.
/// Identifiers and other scalar values remain atomic; only an individually oversized string value
/// is divided into explicitly numbered segments.
/// </summary>
public static class AgentPayloadPaginator
{
    public const int DefaultPageMaxChars = 12_000;

    public static string SerializeCompactDiscovery(
        string query,
        int requestedLimit,
        int totalMatches,
        IEnumerable<object> results,
        string detailTool)
    {
        var materialized = results.ToList();
        return JsonSerializer.Serialize(new
        {
            query,
            resultKind = "compactDiscovery",
            requestedLimit,
            totalMatches,
            returnedCount = materialized.Count,
            isComplete = materialized.Count == totalMatches,
            detailTool,
            note = $"Each result is an explicitly incomplete discovery preview. Use its exact {detailTool} detailReadArguments to retrieve all fields through pagination.",
            results = materialized,
        });
    }

    public static string SerializePage(
        JsonObject identity,
        JsonNode? detail,
        string continuationTool,
        JsonObject continuationArguments,
        int? pageNumber = null,
        int pageMaxChars = DefaultPageMaxChars)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationTool);
        ArgumentNullException.ThrowIfNull(continuationArguments);

        pageMaxChars = Math.Max(1_000, pageMaxChars);
        var entries = new List<JsonObject>();
        Flatten(detail, "$", entries, pageMaxChars);
        if (entries.Count == 0)
            entries.Add(Entry("$", JsonValue.Create((string?)null)));

        var pages = Pack(entries, pageMaxChars);
        var requestedPage = Math.Max(1, pageNumber ?? 1);
        if (requestedPage > pages.Count)
            return $"Error: pageNumber {requestedPage} is beyond this payload's {pages.Count} page(s).";

        var selected = pages[requestedPage - 1];
        var result = (JsonObject)identity.DeepClone();
        result["continuationTool"] = continuationTool;
        result["pagination"] = new JsonObject
        {
            ["currentPage"] = requestedPage,
            ["pageCount"] = pages.Count,
            ["pageMaxChars"] = pageMaxChars,
            ["totalEntries"] = entries.Count,
            ["pageEntryCount"] = selected.Count,
            ["hasPreviousPage"] = requestedPage > 1,
            ["hasNextPage"] = requestedPage < pages.Count,
            ["isComplete"] = pages.Count == 1,
            ["contentFormat"] = "jsonPathEntries",
            ["note"] = pages.Count == 1
                ? "The complete payload is present on this page."
                : "The payload is explicitly paginated; follow previousPageArguments/nextPageArguments to read every entry.",
        };
        result["previousPageArguments"] = requestedPage > 1
            ? PageArguments(continuationArguments, requestedPage - 1)
            : null;
        result["nextPageArguments"] = requestedPage < pages.Count
            ? PageArguments(continuationArguments, requestedPage + 1)
            : null;
        result["content"] = new JsonArray(selected.Select(entry => entry.DeepClone()).ToArray());
        return result.ToJsonString();
    }

    public static JsonObject EntityIdentity(
        Guid id,
        string type,
        string name,
        int? order,
        Guid? parentId,
        params (string Name, JsonNode? Value)[] additionalFields)
    {
        var identity = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["name"] = name,
            ["order"] = order,
            ["parentId"] = parentId,
        };
        foreach (var (fieldName, value) in additionalFields)
            identity[fieldName] = value?.DeepClone();
        return identity;
    }

    private static IReadOnlyList<List<JsonObject>> Pack(IReadOnlyList<JsonObject> entries, int pageMaxChars)
    {
        var pages = new List<List<JsonObject>>();
        var current = new List<JsonObject>();
        var currentChars = 0;
        var contentBudget = Math.Max(500, pageMaxChars - 1_500);
        foreach (var entry in entries)
        {
            var entryChars = entry.ToJsonString().Length + 1;
            if (current.Count > 0 && currentChars + entryChars > contentBudget)
            {
                pages.Add(current);
                current = [];
                currentChars = 0;
            }

            current.Add(entry);
            currentChars += entryChars;
        }

        if (current.Count > 0)
            pages.Add(current);
        return pages.Count == 0 ? [[]] : pages;
    }

    private static void Flatten(JsonNode? node, string path, ICollection<JsonObject> entries, int pageMaxChars)
    {
        switch (node)
        {
            case null:
                entries.Add(Entry(path, null));
                return;
            case JsonObject obj when obj.Count == 0:
                entries.Add(Entry(path, new JsonObject()));
                return;
            case JsonObject obj:
                foreach (var property in obj)
                    AddLogicalEntry(property.Value, PropertyPath(path, property.Key), entries, pageMaxChars);
                return;
            case JsonArray array when array.Count == 0:
                entries.Add(Entry(path, new JsonArray()));
                return;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    AddLogicalEntry(array[index], $"{path}[{index}]", entries, pageMaxChars);
                return;
            case JsonValue value when value.TryGetValue<string>(out var text)
                && Entry(path, JsonValue.Create(text)).ToJsonString().Length > pageMaxChars - 1_500:
                AddStringSegments(path, text, entries, pageMaxChars);
                return;
            default:
                entries.Add(Entry(path, node.DeepClone()));
                return;
        }
    }

    private static void AddLogicalEntry(JsonNode? node, string path, ICollection<JsonObject> entries, int pageMaxChars)
    {
        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && Entry(path, JsonValue.Create(text)).ToJsonString().Length > pageMaxChars - 1_500)
        {
            AddStringSegments(path, text, entries, pageMaxChars);
            return;
        }

        if (node is not JsonArray
            && Entry(path, node?.DeepClone()).ToJsonString().Length <= pageMaxChars - 1_500)
        {
            entries.Add(Entry(path, node?.DeepClone()));
            return;
        }

        Flatten(node, path, entries, pageMaxChars);
    }

    private static void AddStringSegments(string path, string value, ICollection<JsonObject> entries, int pageMaxChars)
    {
        var chunkSize = Math.Max(500, pageMaxChars - 2_500);
        var segments = new List<string>();
        for (var offset = 0; offset < value.Length; offset += chunkSize)
            segments.Add(value.Substring(offset, Math.Min(chunkSize, value.Length - offset)));
        if (segments.Count == 0)
            segments.Add(string.Empty);

        for (var index = 0; index < segments.Count; index++)
        {
            entries.Add(new JsonObject
            {
                ["path"] = path,
                ["segment"] = new JsonObject
                {
                    ["number"] = index + 1,
                    ["count"] = segments.Count,
                    ["startCharacter"] = index * chunkSize,
                    ["endCharacterExclusive"] = Math.Min(value.Length, (index + 1) * chunkSize),
                },
                ["value"] = segments[index],
            });
        }
    }

    private static JsonObject Entry(string path, JsonNode? value) => new()
    {
        ["path"] = path,
        ["value"] = value,
    };

    private static JsonObject PageArguments(JsonObject baseArguments, int pageNumber)
    {
        var arguments = (JsonObject)baseArguments.DeepClone();
        arguments["pageNumber"] = pageNumber;
        return arguments;
    }

    private static string PropertyPath(string parent, string propertyName) =>
        $"{parent}[{JsonSerializer.Serialize(propertyName)}]";
}

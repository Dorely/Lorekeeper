using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lorekeeper.Manuscripts;

public static class ManuscriptSchemaUpgrade
{
    private static readonly HashSet<string> V1BlockTypes = new(
        ["paragraph", "heading", "sceneBreak", "blockQuote", "listItem"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> V1MarkTypes = new(
        ["emphasis", "strong", "underline", "strikethrough", "code", "link", "language"],
        StringComparer.Ordinal);

    public static string UpgradeV1DocumentJson(
        string json,
        Guid expectedManuscriptId,
        long expectedRevision)
    {
        var node = Parse(json);
        if (node is not JsonObject manuscript || !IsV1Manuscript(manuscript))
            throw new InvalidDataException("The import does not contain a manuscript-v1 document.");

        var sourceHash = ProjectV1Hash(manuscript);
        ValidateV1Vocabulary(manuscript);
        AddV2BlockFields(manuscript);
        AddV3BlockFields(manuscript);
        manuscript["schemaVersion"] = ManuscriptDocument.CurrentSchemaVersion;
        var upgradedJson = manuscript.ToJsonString(ManuscriptCodec.JsonOptions);
        var upgraded = ManuscriptCodec.Deserialize(
            upgradedJson,
            expectedManuscriptId,
            expectedRevision);
        if (!string.Equals(
            sourceHash,
            ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(upgraded)),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manuscript-v1 import changed during schema upgrade.");
        }
        return upgradedJson;
    }

    internal static ManuscriptEmbeddedUpgradeResult UpgradeEmbeddedV1Documents(string json)
    {
        var node = Parse(json);
        var sourceHashes = new List<string>();
        var targetHashes = new List<string>();
        var count = UpgradeNode(node, sourceHashes, targetHashes);
        return new ManuscriptEmbeddedUpgradeResult(
            node.ToJsonString(ManuscriptCodec.JsonOptions),
            count,
            sourceHashes,
            targetHashes);
    }

    internal static bool ContainsV1Document(string json)
    {
        try
        {
            return ContainsV1Node(Parse(json));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static bool ContainsStructuredDocument(string json)
    {
        try
        {
            return ContainsStructuredNode(Parse(json));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static bool IsStructuredManuscript(
        string value,
        Guid expectedManuscriptId,
        long? expectedRevision = null)
    {
        JsonObject manuscript;
        try
        {
            manuscript = Parse(value) as JsonObject
                ?? throw new InvalidDataException("The manuscript root is not an object.");
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (!IsV1Manuscript(manuscript) && !IsV2Manuscript(manuscript) && !IsCurrentManuscript(manuscript))
            return false;
        if (!Guid.TryParse(manuscript["manuscriptId"]?.GetValue<string>(), out var manuscriptId)
            || manuscriptId != expectedManuscriptId)
        {
            throw new InvalidDataException(
                $"A structured manuscript does not match its owning chapter {expectedManuscriptId:N}.");
        }
        var revision = manuscript["revision"]?.GetValue<long>()
            ?? throw new InvalidDataException("A structured manuscript has no revision.");
        if (expectedRevision is not null && revision != expectedRevision)
            throw new InvalidDataException("A structured manuscript revision does not match its owning row.");
        if (IsV1Manuscript(manuscript))
        {
            ValidateV1Vocabulary(manuscript);
            return true;
        }
        if (IsV2Manuscript(manuscript))
        {
            _ = UpgradeV2DocumentJson(value, expectedManuscriptId, expectedRevision ?? revision);
            return true;
        }

        ManuscriptCodec.Deserialize(value, expectedManuscriptId, expectedRevision ?? revision);
        return true;
    }

    internal static IReadOnlyList<ManuscriptDocument> ExtractCurrentDocuments(string json)
    {
        var documents = new List<ManuscriptDocument>();
        ExtractCurrentDocuments(Parse(json), documents);
        return documents;
    }

    private static void ExtractCurrentDocuments(
        JsonNode? node,
        List<ManuscriptDocument> documents)
    {
        if (node is JsonObject manuscript && IsCurrentManuscript(manuscript))
        {
            documents.Add(ManuscriptCodec.Deserialize(
                manuscript.ToJsonString(ManuscriptCodec.JsonOptions)));
            return;
        }
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
                ExtractCurrentDocuments(property.Value, documents);
            return;
        }
        if (node is JsonArray array)
        {
            foreach (var item in array)
                ExtractCurrentDocuments(item, documents);
            return;
        }
        if (node is JsonValue value
            && value.TryGetValue<string>(out var embeddedJson)
            && TryParseEmbedded(embeddedJson, out var embedded))
        {
            ExtractCurrentDocuments(embedded, documents);
        }
    }

    private static int UpgradeNode(
        JsonNode? node,
        List<string> sourceHashes,
        List<string> targetHashes)
    {
        if (node is JsonObject manuscript && IsV1Manuscript(manuscript))
        {
            var sourceHash = ProjectV1Hash(manuscript);
            ValidateV1Vocabulary(manuscript);
            AddV2BlockFields(manuscript);
            AddV3BlockFields(manuscript);
            manuscript["schemaVersion"] = ManuscriptDocument.CurrentSchemaVersion;
            var upgraded = ManuscriptCodec.Deserialize(
                manuscript.ToJsonString(ManuscriptCodec.JsonOptions));
            var targetHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(upgraded));
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
                throw new InvalidDataException("A persisted manuscript changed during schema upgrade.");
            sourceHashes.Add(sourceHash);
            targetHashes.Add(targetHash);
            return 1;
        }
        if (node is JsonObject v2Manuscript && IsV2Manuscript(v2Manuscript))
        {
            var sourceHash = ProjectStructuredHash(v2Manuscript);
            AddV3BlockFields(v2Manuscript);
            v2Manuscript["schemaVersion"] = ManuscriptDocument.CurrentSchemaVersion;
            var upgraded = ManuscriptCodec.Deserialize(v2Manuscript.ToJsonString(ManuscriptCodec.JsonOptions));
            var targetHash = ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(upgraded));
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
                throw new InvalidDataException("A persisted manuscript changed during schema-v3 upgrade.");
            sourceHashes.Add(sourceHash);
            targetHashes.Add(targetHash);
            return 1;
        }

        var count = 0;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var embeddedJson)
                    && TryParseEmbedded(embeddedJson, out var embedded)
                    && ContainsV1Node(embedded))
                {
                    var result = UpgradeEmbeddedV1Documents(embeddedJson);
                    obj[property.Key] = result.Json;
                    sourceHashes.AddRange(result.SourceHashes);
                    targetHashes.AddRange(result.TargetHashes);
                    count += result.Count;
                }
                else
                {
                    count += UpgradeNode(property.Value, sourceHashes, targetHashes);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value
                    && value.TryGetValue<string>(out var embeddedJson)
                    && TryParseEmbedded(embeddedJson, out var embedded)
                    && ContainsV1Node(embedded))
                {
                    var result = UpgradeEmbeddedV1Documents(embeddedJson);
                    array[index] = result.Json;
                    sourceHashes.AddRange(result.SourceHashes);
                    targetHashes.AddRange(result.TargetHashes);
                    count += result.Count;
                }
                else
                {
                    count += UpgradeNode(array[index], sourceHashes, targetHashes);
                }
            }
        }
        return count;
    }

    private static bool ContainsV1Node(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (IsV1Manuscript(obj) || IsV2Manuscript(obj))
                return true;
            return obj.Any(property => ContainsV1Node(property.Value));
        }
        if (node is JsonArray array)
            return array.Any(ContainsV1Node);
        return node is JsonValue value
            && value.TryGetValue<string>(out var embeddedJson)
            && TryParseEmbedded(embeddedJson, out var embedded)
            && ContainsV1Node(embedded);
    }

    private static bool ContainsStructuredNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (IsV1Manuscript(obj) || IsV2Manuscript(obj) || IsCurrentManuscript(obj))
                return true;
            return obj.Any(property => ContainsStructuredNode(property.Value));
        }
        if (node is JsonArray array)
            return array.Any(ContainsStructuredNode);
        return node is JsonValue value
            && value.TryGetValue<string>(out var embeddedJson)
            && TryParseEmbedded(embeddedJson, out var embedded)
            && ContainsStructuredNode(embedded);
    }

    private static bool IsV1Manuscript(JsonObject value) =>
        IsManuscriptWithVersion(value, 1);

    private static bool IsV2Manuscript(JsonObject value) =>
        IsManuscriptWithVersion(value, 2);

    private static bool IsCurrentManuscript(JsonObject value) =>
        IsManuscriptWithVersion(value, ManuscriptDocument.CurrentSchemaVersion);

    private static bool IsManuscriptWithVersion(JsonObject value, int version) =>
        value["schemaVersion"] is JsonValue schemaVersion
        && schemaVersion.TryGetValue<int>(out var parsedVersion)
        && parsedVersion == version
        && value["manuscriptId"] is JsonValue
        && value["revision"] is JsonValue
        && value["content"] is JsonArray;

    private static string ProjectV1Hash(JsonObject manuscript)
    {
        var blocks = manuscript["content"] as JsonArray
            ?? throw new InvalidDataException("A manuscript-v1 document has no content array.");
        var plainText = string.Join(
            "\n\n",
            blocks.Select(block =>
            {
                var obj = block as JsonObject
                    ?? throw new InvalidDataException("A manuscript-v1 block is not an object.");
                if (string.Equals(obj["type"]?.GetValue<string>(), "sceneBreak", StringComparison.Ordinal))
                    return "***";
                var content = obj["content"] as JsonArray
                    ?? throw new InvalidDataException("A manuscript-v1 block has no inline content.");
                return string.Concat(content.Select(inline =>
                    (inline as JsonObject)?["text"]?.GetValue<string>()
                    ?? throw new InvalidDataException("A manuscript-v1 inline node has no text.")));
            }));
        return ManuscriptCodec.HashPlainText(plainText);
    }

    private static void ValidateV1Vocabulary(JsonObject manuscript)
    {
        var blocks = manuscript["content"] as JsonArray
            ?? throw new InvalidDataException("A manuscript-v1 document has no content array.");
        foreach (var blockNode in blocks)
        {
            var block = blockNode as JsonObject
                ?? throw new InvalidDataException("A manuscript-v1 block is not an object.");
            var blockType = block["type"]?.GetValue<string>();
            if (blockType is null || !V1BlockTypes.Contains(blockType))
                throw new InvalidDataException($"Manuscript-v1 contains unsupported block type '{blockType}'.");
            var content = block["content"] as JsonArray
                ?? throw new InvalidDataException("A manuscript-v1 block has no inline content.");
            foreach (var inlineNode in content)
            {
                var inline = inlineNode as JsonObject
                    ?? throw new InvalidDataException("A manuscript-v1 inline node is not an object.");
                if (inline["marks"] is not JsonArray marks)
                    throw new InvalidDataException("A manuscript-v1 inline node has no mark collection.");
                foreach (var markNode in marks)
                {
                    var markType = (markNode as JsonObject)?["type"]?.GetValue<string>();
                    if (markType is null || !V1MarkTypes.Contains(markType))
                        throw new InvalidDataException(
                            $"Manuscript-v1 contains unsupported mark type '{markType}'.");
                }
            }
        }
    }

    private static void AddV2BlockFields(JsonObject manuscript)
    {
        var blocks = manuscript["content"] as JsonArray
            ?? throw new InvalidDataException("A manuscript-v1 document has no content array.");
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var legacyStyleRole = block["styleRole"]?.GetValue<string>()
                ?? throw new InvalidDataException("A manuscript-v1 block has no semantic style role.");
            block["headingLevel"] = string.Equals(
                block["type"]?.GetValue<string>(),
                "heading",
                StringComparison.Ordinal)
                ? string.Equals(
                    legacyStyleRole,
                    ManuscriptStyleRoles.ChapterHeading,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 2
                : null;
            block["styleRole"] = ManuscriptSemanticRoles.NormalizeLegacy(legacyStyleRole);
            block["imageId"] = null;
            block["altText"] = null;
        }
    }

    public static string UpgradeV2DocumentJson(
        string json,
        Guid expectedManuscriptId,
        long expectedRevision)
    {
        var manuscript = Parse(json) as JsonObject
            ?? throw new InvalidDataException("The manuscript root is not an object.");
        if (!IsV2Manuscript(manuscript))
            throw new InvalidDataException("The import does not contain a manuscript-v2 document.");
        var sourceHash = ProjectStructuredHash(manuscript);
        AddV3BlockFields(manuscript);
        manuscript["schemaVersion"] = ManuscriptDocument.CurrentSchemaVersion;
        var upgradedJson = manuscript.ToJsonString(ManuscriptCodec.JsonOptions);
        var upgraded = ManuscriptCodec.Deserialize(upgradedJson, expectedManuscriptId, expectedRevision);
        if (!string.Equals(sourceHash, ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(upgraded)), StringComparison.Ordinal))
            throw new InvalidDataException("Manuscript-v2 import changed during schema upgrade.");
        return upgradedJson;
    }

    private static string ProjectStructuredHash(JsonObject manuscript)
    {
        var content = manuscript["content"] as JsonArray
            ?? throw new InvalidDataException("A structured manuscript has no content array.");
        var text = string.Join("\n\n", content.Select(blockNode =>
        {
            var block = blockNode as JsonObject
                ?? throw new InvalidDataException("A structured manuscript block is not an object.");
            if (string.Equals(block["type"]?.GetValue<string>(), "sceneBreak", StringComparison.Ordinal))
                return "***";
            var inlineContent = block["content"] as JsonArray
                ?? throw new InvalidDataException("A structured manuscript block has no content array.");
            return string.Concat(inlineContent.Select(inline =>
                (inline as JsonObject)?["text"]?.GetValue<string>() ?? string.Empty));
        }));
        return ManuscriptCodec.HashPlainText(text);
    }

    private static void AddV3BlockFields(JsonObject manuscript)
    {
        var blocks = manuscript["content"] as JsonArray
            ?? throw new InvalidDataException("A structured manuscript has no content array.");
        foreach (var block in blocks.OfType<JsonObject>())
        {
            var isFigure = string.Equals(block["type"]?.GetValue<string>(), "figure", StringComparison.Ordinal);
            block["decorative"] = false;
            block["figurePresentation"] = isFigure
                ? JsonSerializer.SerializeToNode(new FigurePresentation(), ManuscriptCodec.JsonOptions)
                : null;
            block["accessibilityRole"] = isFigure ? "figure" : null;
            block["pageCompositionId"] = null;
        }
    }

    private static JsonNode Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json)
                ?? throw new InvalidDataException("The JSON value is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The JSON value is malformed.", exception);
        }
    }

    private static bool TryParseEmbedded(string value, out JsonNode? node)
    {
        node = null;
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;
        try
        {
            node = JsonNode.Parse(value);
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed record ManuscriptEmbeddedUpgradeResult(
    string Json,
    int Count,
    IReadOnlyList<string> SourceHashes,
    IReadOnlyList<string> TargetHashes);

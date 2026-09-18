using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;

namespace Lorekeeper.Context;

/// <summary>
/// Builds the compact, model-facing representation of a semantic manuscript.
/// This is deliberately separate from <see cref="ManuscriptCodec"/>'s canonical
/// persistence, import/export, and audit representation.
/// </summary>
internal static class AgentManuscriptProjection
{
    public const string Schema = "agent-manuscript-v3";
    public const string StylesSchema = "agent-manuscript-styles-v1";

    public static string SerializeCurrentChapter(Chapter chapter, ManuscriptSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        if (snapshot is null)
        {
            var unavailable = new JsonObject
            {
                ["schema"] = Schema,
                ["source"] = "unavailable",
                ["complete"] = false,
                ["chapter"] = new JsonObject
                {
                    ["id"] = chapter.Id,
                    ["title"] = chapter.Title,
                },
                ["note"] = "No structured manuscript snapshot was available. Use the manuscript read tool before editing.",
            };
            if (!string.IsNullOrEmpty(chapter.PlainText))
                unavailable["plainText"] = chapter.PlainText;
            return Serialize(unavailable);
        }

        var payload = CreateDocument(
            snapshot.Document,
            source: "persisted",
            chapterId: chapter.Id,
            chapterTitle: chapter.Title,
            sourceHash: snapshot.SourceHash);
        payload["note"] = "This is the authoritative active manuscript snapshot for this turn. Use its revision and stable block IDs directly for semantic edits. Read the manuscript again only if this snapshot is missing, incomplete, or stale.";
        return Serialize(payload);
    }

    public static string SerializeDocument(
        ManuscriptDocument document,
        string source,
        int startBlock = 0,
        int? blockCount = null,
        Guid? chapterId = null,
        string? chapterTitle = null,
        string? sourceHash = null) =>
        Serialize(CreateDocument(
            document,
            source,
            startBlock,
            blockCount,
            chapterId,
            chapterTitle,
            sourceHash));

    public static string SerializeInspection(
        ManuscriptDocument document,
        string source,
        ManuscriptInspectionResult inspection,
        Guid? chapterId = null,
        string? chapterTitle = null,
        string? sourceHash = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inspection);

        var indexesByBlock = new Dictionary<ManuscriptBlock, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < document.Content.Count; index++)
            indexesByBlock[document.Content[index]] = index;
        var matches = inspection.Matches
            .Where(indexesByBlock.ContainsKey)
            .Select(block => (block, indexesByBlock[block]))
            .ToList();
        var pagination = new JsonObject
        {
            ["startMatch"] = inspection.Start,
            ["returnedBlockCount"] = matches.Count,
            ["totalMatches"] = inspection.MatchCount,
            ["totalBlockCount"] = document.Content.Count,
            ["hasMore"] = inspection.HasMore,
        };
        if (inspection.HasMore)
            pagination["nextStartMatch"] = inspection.Start + matches.Count;

        var payload = CreateDocument(
            document,
            source,
            matches,
            chapterId,
            chapterTitle,
            sourceHash,
            pagination);
        payload["inspection"] = JsonSerializer.SerializeToNode(new
        {
            inspection.IsValid,
            inspection.Diagnostics,
            inspection.DiagnosticCount,
        }, ContextPayloadJson.Options);
        return Serialize(payload);
    }

    public static string SerializeStyles(IReadOnlyList<ManuscriptStyleView> styles)
    {
        ArgumentNullException.ThrowIfNull(styles);

        var payload = new JsonObject
        {
            ["schema"] = StylesSchema,
            ["note"] = "Named styles are reusable typography definitions. In a manuscript, direct paragraph formatting overrides its named style, and a named style overrides built-in defaults.",
        };
        if (styles.Count > 0)
        {
            var namedStyles = new JsonArray();
            foreach (var style in styles)
                namedStyles.Add(StylePayload(style));
            payload["namedStyles"] = namedStyles;
        }

        return Serialize(payload);
    }

    private static JsonObject CreateDocument(
        ManuscriptDocument document,
        string source,
        int startBlock = 0,
        int? blockCount = null,
        Guid? chapterId = null,
        string? chapterTitle = null,
        string? sourceHash = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var total = document.Content.Count;
        startBlock = Math.Clamp(startBlock, 0, total);
        var requestedCount = blockCount is null
            ? total - startBlock
            : Math.Clamp(blockCount.Value, 1, 100);
        var blocks = document.Content
            .Select((block, index) => (block, index))
            .Skip(startBlock)
            .Take(requestedCount)
            .ToList();
        var hasMore = startBlock + blocks.Count < total;
        var pagination = new JsonObject
        {
            ["startBlock"] = startBlock,
            ["returnedBlockCount"] = blocks.Count,
            ["totalBlockCount"] = total,
            ["hasMore"] = hasMore,
        };
        if (hasMore)
            pagination["nextStartBlock"] = startBlock + blocks.Count;

        return CreateDocument(
            document,
            source,
            blocks,
            chapterId,
            chapterTitle,
            sourceHash,
            pagination);
    }

    private static JsonObject CreateDocument(
        ManuscriptDocument document,
        string source,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks,
        Guid? chapterId,
        string? chapterTitle,
        string? sourceHash,
        JsonObject pagination)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var payload = new JsonObject
        {
            ["schema"] = Schema,
            ["manuscriptId"] = document.ManuscriptId,
            ["manuscriptSchemaVersion"] = document.SchemaVersion,
            ["revision"] = document.Revision,
            ["sourceHash"] = sourceHash ?? ManuscriptCodec.HashPlainText(ManuscriptCodec.ProjectPlainText(document)),
            ["source"] = source,
            ["complete"] = IsCompleteDocument(blocks, document.Content.Count),
            ["pagination"] = pagination,
        };
        if (chapterId is Guid || !string.IsNullOrWhiteSpace(chapterTitle))
        {
            var chapter = new JsonObject();
            if (chapterId is Guid chapterValue)
                chapter["id"] = chapterValue;
            if (!string.IsNullOrWhiteSpace(chapterTitle))
                chapter["title"] = chapterTitle;
            payload["chapter"] = chapter;
        }

        var blockRows = new JsonArray();
        foreach (var (block, index) in blocks)
        {
            blockRows.Add(new JsonArray
            {
                index,
                block.Id,
                ExactText(block),
            });
        }
        payload["blocks"] = blockRows;

        AddStructure(payload, blocks);
        AddMarks(payload, blocks);
        AddParagraphFormatting(payload, blocks);
        AddFigures(payload, blocks);
        AddDesignedPages(payload, blocks);
        AddPublicationFields(payload, blocks);
        AddRichContent(payload, document, blocks);
        return payload;
    }

    private static bool IsCompleteDocument(
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks,
        int totalBlockCount) =>
        blocks.Count == totalBlockCount
        && blocks.Select(item => item.index).SequenceEqual(Enumerable.Range(0, totalBlockCount));

    private static void AddStructure(
        JsonObject payload,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var structure = new JsonArray();
        foreach (var (block, index) in blocks)
        {
            if (block.Type == ManuscriptBlockType.Paragraph
                && string.Equals(block.StyleRole, ManuscriptStyleRoles.Body, StringComparison.OrdinalIgnoreCase)
                && block.HeadingLevel is null)
            {
                continue;
            }

            var row = new JsonArray
            {
                index,
                EnumName(block.Type),
                block.StyleRole,
            };
            if (block.HeadingLevel is int headingLevel)
                row.Add(headingLevel);
            structure.Add(row);
        }

        if (structure.Count > 0)
            payload["structure"] = structure;
    }

    private static void AddMarks(
        JsonObject payload,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var marks = new JsonArray();
        foreach (var (block, index) in blocks)
        {
            var offset = 0;
            foreach (var inline in block.Content)
            {
                var end = offset + inline.Text.Length;
                foreach (var mark in inline.Marks)
                {
                    var row = new JsonArray
                    {
                        index,
                        offset,
                        end,
                        EnumName(mark.Type),
                    };
                    if (mark.Value is not null)
                        row.Add(mark.Value);
                    marks.Add(row);
                }
                offset = end;
            }
        }

        if (marks.Count > 0)
            payload["marksUtf16"] = marks;
    }

    private static void AddParagraphFormatting(
        JsonObject payload,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var formats = new JsonArray();
        var formatIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var formatting = new JsonArray();
        foreach (var (block, index) in blocks)
        {
            if (block.ParagraphPresentation is null)
                continue;

            var format = SparseParagraphFormat(block.ParagraphPresentation);
            var json = format.ToJsonString(ContextPayloadJson.Options);
            if (!formatIndexes.TryGetValue(json, out var formatIndex))
            {
                formatIndex = formats.Count;
                formatIndexes.Add(json, formatIndex);
                formats.Add(format);
            }
            formatting.Add(new JsonArray { index, formatIndex });
        }

        if (formats.Count > 0)
        {
            payload["paragraphFormats"] = formats;
            payload["paragraphFormatting"] = formatting;
        }
    }

    private static void AddFigures(
        JsonObject payload,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var figures = new JsonArray();
        foreach (var (block, index) in blocks.Where(item => item.block.Type == ManuscriptBlockType.Figure))
        {
            var figure = new JsonObject
            {
                ["block"] = index,
                ["decorative"] = block.Decorative,
            };
            if (block.ImageId is Guid imageId)
                figure["imageId"] = imageId;
            if (block.AltText is not null)
                figure["altText"] = block.AltText;
            if (block.Language is not null)
                figure["language"] = block.Language;
            if (block.AccessibilityRole is not null)
                figure["accessibilityRole"] = EnumName(block.AccessibilityRole.Value);
            if (block.FigurePresentation is not null)
                figure["presentation"] = JsonSerializer.SerializeToNode(block.FigurePresentation, ContextPayloadJson.Options);
            figures.Add(figure);
        }

        if (figures.Count > 0)
            payload["figures"] = figures;
    }

    private static void AddDesignedPages(
        JsonObject payload,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var pages = new JsonArray();
        foreach (var (block, index) in blocks.Where(item => item.block.Type == ManuscriptBlockType.DesignedPage))
        {
            // The manuscript block is the placement identity.  A Designed Page may
            // occur more than once, so callers must never use the page ID to locate
            // a particular occurrence.
            var row = new JsonArray { index, block.Id };
            if (block.DesignedPageId is Guid pageId)
                row.Add(pageId);
            pages.Add(row);
        }

        if (pages.Count > 0)
            payload["designedPages"] = pages;
    }

    private static void AddPublicationFields(
        JsonObject payload,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var fields = new JsonArray();
        foreach (var (block, index) in blocks.Where(item => item.block.PublicationField is not null))
        {
            fields.Add(new JsonArray
            {
                index,
                EnumName(block.PublicationField!.Value),
            });
        }

        if (fields.Count > 0)
            payload["publicationFields"] = fields;
    }

    private static void AddRichContent(
        JsonObject payload,
        ManuscriptDocument document,
        IReadOnlyList<(ManuscriptBlock block, int index)> blocks)
    {
        var tables = new JsonArray();
        foreach (var (block, index) in blocks.Where(item => item.block.Type == ManuscriptBlockType.Table))
        {
            tables.Add(new JsonObject
            {
                ["block"] = index,
                ["table"] = JsonSerializer.SerializeToNode(block.Table, ContextPayloadJson.Options),
            });
        }
        if (tables.Count > 0)
            payload["tables"] = tables;

        if (document.Notes.Count > 0)
            payload["notes"] = JsonSerializer.SerializeToNode(document.Notes, ContextPayloadJson.Options);

        var positions = new JsonArray();
        foreach (var segment in ManuscriptTraversal.EnumerateText(document))
        {
            positions.Add(new JsonArray
            {
                JsonSerializer.SerializeToNode(segment.Start.ContainerPath, ContextPayloadJson.Options),
                segment.Start.BlockOrAtomId,
                segment.Text.Length,
            });
        }
        if (positions.Count > 0)
            payload["positionsUtf16"] = positions;
    }

    private static JsonObject StylePayload(ManuscriptStyleView style) => new()
    {
        ["id"] = style.Id,
        ["name"] = style.Name,
        ["kind"] = EnumName(style.Kind),
        ["semanticRole"] = style.SemanticRole,
        ["definition"] = SparseStyleDefinition(style.Definition),
        ["revision"] = style.Revision,
    };

    private static JsonObject SparseParagraphFormat(ParagraphPresentation presentation) =>
        JsonSerializer.SerializeToNode(presentation, ContextPayloadJson.Options)?.AsObject()
        ?? new JsonObject();

    private static JsonObject SparseStyleDefinition(ManuscriptStyleProperties definition) =>
        JsonSerializer.SerializeToNode(definition, ContextPayloadJson.Options)?.AsObject()
        ?? new JsonObject();

    private static string ExactText(ManuscriptBlock block) => ManuscriptCodec.Text(block);

    private static string EnumName<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string Serialize(JsonObject payload) =>
        payload.ToJsonString(ContextPayloadJson.Options);
}

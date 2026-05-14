using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.EditorChat;

public sealed class EditorChatTools(
    IChapterService chapters,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IProjectFactService projectFacts,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    IProjectRepository projects,
    IEditorContextService editorContext,
    IEntityRelationContextService entityRelations,
    OutlineCollaborationTools outlineTools,
    IOptions<EditorChatOptions> editorOptions)
{
    private static readonly EntityRelationContextOptions _listEntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    private static readonly EntityRelationContextOptions _detailEntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 16,
        MaxTraversalPaths = 24,
        MaxLinksPerNode = 10,
    };

    public IList<AITool> Build(EditorChatContext context, EditorChatToolMode mode = EditorChatToolMode.Normal)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: () => ListContextAsync(context),
                name: "list_context",
                description: "Read the currently assembled editor context exactly as the model sees it, including enabled outline, facts, writing samples, and selected entities."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 8) => VectorSearchAsync(context, query, topK),
                name: "vector_search",
                description: "Semantic search over indexed chapters and lore for the current project. Returns the top matching snippets with their source metadata."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List every chapter in the current project (id, order, title, synopsis, body line count, and read_chapter page count)."),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(context),
                name: "list_project_facts",
                description: "List project-level facts and their linked graph entities as JSON."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 10) => SearchEntitiesAsync(context, query, topK),
                name: "search_entities",
                description: "Search story graph entities by name, type, and property text. Use this when you need a specific character, location, beat, or custom entity but do not know its id."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ReadEntityAsync(context, entityId),
                name: "read_entity",
                description: "Read one graph entity by id, including properties and adjacent links."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityLinksAsync(context, entityId),
                name: "list_entity_links",
                description: "List all graph links adjacent to an entity, including structural HasChild links and semantic story relationships."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? startLine = null, int? endLine = null, int? pageNumber = null) =>
                    ReadChapterAsync(context, chapterId, startLine, endLine, pageNumber),
                name: "read_chapter",
                description:
                    "Read one paginated page of a chapter's current body with line numbers (0001: ...). " +
                    "Use list_chapters to discover ids and page counts. Omit startLine/endLine to page through the full chapter; " +
                    "provide startLine and/or endLine for an inclusive line range; provide pageNumber to read a specific page within that requested full/range scope. " +
                    "Always returns content plus pagination metadata. If this turn already staged an edit to the chapter, returns the latest staged body for this turn."),
        };

        if (mode == EditorChatToolMode.ContestPreparation)
        {
            tools.Add(AIFunctionFactory.Create(
                method: (Guid chapterId) => StartContestAsync(context, chapterId),
                name: "start_contest",
                description:
                    "Start a Contest Mode generation job for chapter-body mutations. " +
                    "Call this exactly once after gathering enough read-only context. "));
            return tools;
        }

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, string content, int? startLine = null, int? endLine = null) =>
                EditChapterAsync(context, chapterId, content, startLine, endLine),
            name: "edit_chapter",
            description:
                "Edit a chapter using line-based semantics. " +
                "To Append: Leave both startLine and endLine null: appends `content` to the end of the chapter. " +
                "To Insert: Provide only startLine and leave endLine null: insert `content` BEFORE that line (1-based). " +
                "To Replace: Provide both startLine and endLine: replace the inclusive range of existing numbered lines with `content`. " +
                "Lines are 1-based and match the numbering shown by read_chapter and the editor gutter. " +
                "`content` should not contain line numbers. " +
                "Returns the new line-numbered body and a short change summary."));

        var existingNames = tools.OfType<AIFunction>().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var outlineTool in outlineTools.Build(new OutlineCollaborationContext(context.ProjectId, context.OnMutated, context.OutlineStaging)))
        {
            if (outlineTool is AIFunction function && existingNames.Add(function.Name))
                tools.Add(outlineTool);
        }

        return tools;
    }

    private async Task<string> ListContextAsync(EditorChatContext ctx)
    {
        var project = await projects.GetByIdAsync(ctx.ProjectId)
            ?? throw new InvalidOperationException($"Project {ctx.ProjectId} not found.");
        Chapter? currentChapter = null;
        if (ctx.CurrentChapterId is { } chapterId)
            currentChapter = await chapters.GetAsync(chapterId);

        var assembly = await editorContext.BuildAsync(project, currentChapter);
        return assembly.Assemble();
    }

    private async Task<string> VectorSearchAsync(
        EditorChatContext ctx,
        [Description("Natural-language query to embed and search.")] string query,
        [Description("Maximum number of results to return (1-20).")] int topK)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        var embedding = await embeddings.GenerateEmbeddingAsync(query);
        var results = await vectors.SearchAsync(embedding, Project.ScopeKey(ctx.ProjectId), topK);

        if (results.Count == 0) return "No matches.";

        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            sb.Append('[').Append(i + 1).Append("] ")
              .Append(result.SourceType).Append('/').Append(result.SourceId ?? "?")
              .Append(" row=").Append(result.RowId);
            if (result.ChunkIndex is not null)
                sb.Append(" fragment=").Append(result.ChunkIndex.Value + 1);
            if (!string.IsNullOrWhiteSpace(result.Metadata))
                sb.Append(" - ").Append(result.Metadata);
            sb.Append(" (distance ").Append(result.Distance.ToString("F4")).Append(")\n");
            sb.Append(result.Content).Append("\n\n");

            if (string.Equals(result.SourceType, ContextVectorSourceTypes.Entity, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(result.SourceId)
                && Guid.TryParseExact(result.SourceId, "N", out var entityId))
            {
                var relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, entityId, _listEntityRelationOptions);
                if (relationContext.DirectLinks.Count > 0 || relationContext.TraversalMap.Count > 0)
                {
                    sb.AppendLine("Relation context:");
                    foreach (var link in relationContext.DirectLinks)
                        sb.Append("- ").AppendLine(link.Path);
                    if (relationContext.TraversalMap.Count > 0)
                        sb.AppendLine(entityRelations.FormatTraversalMap(relationContext.TraversalMap));
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListChaptersAsync(EditorChatContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var sb = new StringBuilder();
        foreach (var chapter in list)
        {
            var lineCount = ChapterFormatting.SplitLines(chapter.Body).Count;
            var pageCount = CountReadChapterPages(chapter.Body, EffectiveReadChapterPageMaxChars());

            sb.Append(chapter.Order + 1).Append(". ").Append(chapter.Title)
              .Append(" - id=").Append(chapter.Id)
              .Append(" - lines=").Append(lineCount)
              .Append(" - bodyChars=").Append(chapter.Body.Length)
              .Append(" - readChapterPages=").Append(pageCount);
            if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
                sb.Append(" - ").Append(chapter.Synopsis);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListProjectFactsAsync(EditorChatContext ctx)
    {
        var facts = await projectFacts.ListAsync(ctx.ProjectId);
        var payload = new List<object>();
        foreach (var fact in facts)
        {
            var linkedEntities = new List<object>();
            foreach (var link in fact.LinkedEntities)
            {
                linkedEntities.Add(new
                {
                    link.EdgeType,
                    direction = link.Direction.ToString(),
                    link.EntityId,
                    link.EntityName,
                    link.EntityType,
                    relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, link.EntityId, _listEntityRelationOptions),
                });
            }

            payload.Add(new
            {
                fact.Id,
                fact.Key,
                fact.Name,
                fact.Value,
                linkedEntities,
            });
        }

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string> SearchEntitiesAsync(EditorChatContext ctx, string query, int topK)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);
        var searchTerms = SearchTerms(query);

        var matches = new List<(StoryEntity Entity, int Score)>();
        var types = await entityTypes.ListAsync(ctx.ProjectId, includeStructural: true);
        foreach (var type in types.Where(type => IsSearchableEntityType(type.Type)))
        {
            var list = await entities.ListAsync(ctx.ProjectId, type.Type);
            matches.AddRange(list
                .Select(entity => (Entity: entity, Score: SearchScore(entity, query, searchTerms)))
                .Where(match => match.Score > 0));
        }

        var payload = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select(match => CompactEntitySearchPayload(match.Entity, match.Score));

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string> ReadEntityAsync(EditorChatContext ctx, Guid entityId)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        var relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, entityId, _detailEntityRelationOptions);
        return JsonSerializer.Serialize(new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            links = links.Select(link => new
            {
                link.EdgeId,
                link.EdgeType,
                direction = link.Direction.ToString(),
                link.OtherEntityId,
                link.OtherEntityName,
                link.OtherEntityType,
                link.SortOrder,
                link.Properties,
            }),
            relationContext,
        });
    }

    private async Task<object> EntityPayloadAsync(
        Guid projectId,
        StoryEntity entity,
        EntityRelationContextOptions relationOptions)
    {
        var relationContext = await entityRelations.BuildForEntityAsync(projectId, entity.Id, relationOptions);
        return new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            relationContext,
        };
    }

    private async Task<string> ListEntityLinksAsync(EditorChatContext ctx, Guid entityId)
    {
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        return JsonSerializer.Serialize(links.Select(link => new
        {
            edgeId = link.EdgeId,
            edgeType = link.EdgeType,
            direction = link.Direction.ToString(),
            otherEntityId = link.OtherEntityId,
            otherEntityName = link.OtherEntityName,
            otherEntityType = link.OtherEntityType,
            sortOrder = link.SortOrder,
            properties = link.Properties,
        }));
    }

    private async Task<string> ReadChapterAsync(
        EditorChatContext ctx,
        Guid chapterId,
        int? startLine,
        int? endLine,
        int? pageNumber)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var body = chapter.Body;
        var source = "persisted";
        if (ctx.ReviewEdits && ctx.EditorStaging?.TryGetChapterBodyDraft(chapter.Id, out var draftBody) == true)
        {
            body = draftBody;
            source = "stagedDraft";
        }

        var requestedPageNumber = pageNumber ?? 1;
        if (requestedPageNumber < 1)
            return "Error: pageNumber must be 1 or greater.";

        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var lines = ChapterFormatting.SplitLines(body);
        var rangeError = TryResolveReadChapterRange(lines, startLine, endLine, out var range);
        if (rangeError is not null)
            return rangeError;

        if (lines.Count == 0)
        {
            if (requestedPageNumber > 1)
                return "Error: pageNumber 1 is the only page available for an empty chapter.";

            return JsonSerializer.Serialize(new
            {
                chapter = new
                {
                    id = chapter.Id,
                    chapter.Title,
                    chapter.Synopsis,
                    source,
                },
                request = new
                {
                    startLine,
                    endLine,
                    pageNumber = requestedPageNumber,
                },
                range = new
                {
                    rangeStartLine = 0,
                    rangeEndLine = 0,
                    rangeLineCount = 0,
                    totalChapterLines = 0,
                },
                pagination = new
                {
                    pageStartLine = 0,
                    pageEndLine = 0,
                    pageStartColumn = 0,
                    pageEndColumn = 0,
                    currentPage = 1,
                    pageCount = 1,
                    pageMaxChars,
                    contentCharCount = 7,
                    hasPreviousPage = false,
                    hasNextPage = false,
                    previousPageNumber = (int?)null,
                    nextPageNumber = (int?)null,
                    startsInsideLine = false,
                    endsInsideLine = false,
                    containsPartialLine = false,
                },
                previousPageArguments = (object?)null,
                nextPageArguments = (object?)null,
                content = "(empty)",
            });
        }

        var pages = BuildReadChapterPages(lines, range.StartLine, range.EndLine, pageMaxChars);
        if (requestedPageNumber > pages.Count)
            return $"Error: pageNumber {requestedPageNumber} is beyond the {pages.Count} page(s) available for lines {range.StartLine}-{range.EndLine}.";

        var selectedPage = pages[requestedPageNumber - 1];
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var content = FormatReadChapterPageContent(selectedPage, lineNumberWidth);

        return JsonSerializer.Serialize(new
        {
            chapter = new
            {
                id = chapter.Id,
                chapter.Title,
                chapter.Synopsis,
                source,
            },
            request = new
            {
                startLine,
                endLine,
                pageNumber = requestedPageNumber,
            },
            range = new
            {
                rangeStartLine = range.StartLine,
                rangeEndLine = range.EndLine,
                rangeLineCount = range.LineCount,
                totalChapterLines = lines.Count,
            },
            pagination = new
            {
                pageStartLine = selectedPage.PageStartLine,
                pageEndLine = selectedPage.PageEndLine,
                pageStartColumn = selectedPage.PageStartColumn,
                pageEndColumn = selectedPage.PageEndColumn,
                currentPage = requestedPageNumber,
                pageCount = pages.Count,
                pageMaxChars,
                contentCharCount = content.Length,
                hasPreviousPage = requestedPageNumber > 1,
                hasNextPage = requestedPageNumber < pages.Count,
                previousPageNumber = requestedPageNumber > 1 ? requestedPageNumber - 1 : (int?)null,
                nextPageNumber = requestedPageNumber < pages.Count ? requestedPageNumber + 1 : (int?)null,
                startsInsideLine = selectedPage.StartsInsideLine,
                endsInsideLine = selectedPage.EndsInsideLine,
                containsPartialLine = selectedPage.ContainsPartialLine,
            },
            previousPageArguments = requestedPageNumber > 1
                ? new { chapterId = chapter.Id, startLine = range.StartLine, endLine = range.EndLine, pageNumber = requestedPageNumber - 1 }
                : null,
            nextPageArguments = requestedPageNumber < pages.Count
                ? new { chapterId = chapter.Id, startLine = range.StartLine, endLine = range.EndLine, pageNumber = requestedPageNumber + 1 }
                : null,
            content,
        });
    }

    private int EffectiveReadChapterPageMaxChars() => Math.Max(256, editorOptions.Value.ReadChapterPageMaxChars);

    private static int CountReadChapterPages(string body, int pageMaxChars)
    {
        var lines = ChapterFormatting.SplitLines(body);
        return lines.Count == 0
            ? 1
            : BuildReadChapterPages(lines, 1, lines.Count, pageMaxChars).Count;
    }

    private static string? TryResolveReadChapterRange(
        IReadOnlyList<string> lines,
        int? startLine,
        int? endLine,
        out ReadChapterRange range)
    {
        range = new ReadChapterRange(0, 0, 0);

        if (lines.Count == 0)
        {
            if (startLine is not null || endLine is not null)
                return "Error: chapter is empty; no line range can be read.";

            return null;
        }

        var start = startLine ?? 1;
        var end = endLine ?? lines.Count;
        if (start < 1) return "Error: startLine must be 1 or greater.";
        if (end < 1) return "Error: endLine must be 1 or greater.";
        if (start > end) return "Error: startLine must be less than or equal to endLine.";
        if (start > lines.Count) return $"Error: startLine {start} is beyond the chapter's {lines.Count} lines.";

        end = Math.Min(end, lines.Count);
        range = new ReadChapterRange(start, end, end - start + 1);
        return null;
    }

    private static List<ReadChapterPage> BuildReadChapterPages(
        IReadOnlyList<string> lines,
        int startLine,
        int endLine,
        int pageMaxChars)
    {
        var pages = new List<ReadChapterPage>();
        var currentPage = new ReadChapterPage();
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var prefixLength = lineNumberWidth + 2;

        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            var text = lines[lineNumber - 1];
            var fullLineLength = prefixLength + text.Length;
            if (fullLineLength <= pageMaxChars)
            {
                var addLength = fullLineLength + (currentPage.Segments.Count == 0 ? 0 : 1);
                if (currentPage.Segments.Count > 0 && currentPage.ContentCharCount + addLength > pageMaxChars)
                {
                    pages.Add(currentPage);
                    currentPage = new ReadChapterPage();
                }

                currentPage.Add(new ReadChapterLineSegment(
                    lineNumber,
                    text,
                    StartColumn: 1,
                    EndColumn: text.Length,
                    LineLength: text.Length,
                    IsFullLine: true), lineNumberWidth);
                continue;
            }

            if (currentPage.Segments.Count > 0)
            {
                pages.Add(currentPage);
                currentPage = new ReadChapterPage();
            }

            var maxTextChars = Math.Max(1, pageMaxChars - prefixLength);
            for (var offset = 0; offset < text.Length; offset += maxTextChars)
            {
                var length = Math.Min(maxTextChars, text.Length - offset);
                var segment = new ReadChapterLineSegment(
                    lineNumber,
                    text.Substring(offset, length),
                    StartColumn: offset + 1,
                    EndColumn: offset + length,
                    LineLength: text.Length,
                    IsFullLine: false);

                var partialPage = new ReadChapterPage();
                partialPage.Add(segment, lineNumberWidth);
                pages.Add(partialPage);
            }
        }

        if (currentPage.Segments.Count > 0)
            pages.Add(currentPage);

        return pages;
    }

    private static string FormatReadChapterPageContent(ReadChapterPage page, int lineNumberWidth)
    {
        var sb = new StringBuilder(page.ContentCharCount);
        for (var i = 0; i < page.Segments.Count; i++)
        {
            var segment = page.Segments[i];
            sb.Append(segment.LineNumber.ToString().PadLeft(lineNumberWidth, '0'));
            sb.Append(": ");
            sb.Append(segment.Text);
            if (i < page.Segments.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    private sealed record ReadChapterRange(int StartLine, int EndLine, int LineCount);

    private sealed record ReadChapterLineSegment(
        int LineNumber,
        string Text,
        int StartColumn,
        int EndColumn,
        int LineLength,
        bool IsFullLine);

    private sealed class ReadChapterPage
    {
        public List<ReadChapterLineSegment> Segments { get; } = [];
        public int ContentCharCount { get; private set; }

        public int PageStartLine => Segments[0].LineNumber;
        public int PageEndLine => Segments[^1].LineNumber;
        public int PageStartColumn => Segments[0].StartColumn;
        public int PageEndColumn => Segments[^1].EndColumn;
        public bool StartsInsideLine => Segments[0].StartColumn > 1;
        public bool EndsInsideLine => Segments[^1].EndColumn < Segments[^1].LineLength;
        public bool ContainsPartialLine => Segments.Any(segment => !segment.IsFullLine);

        public void Add(ReadChapterLineSegment segment, int lineNumberWidth)
        {
            if (Segments.Count > 0) ContentCharCount++;
            ContentCharCount += lineNumberWidth + 2 + segment.Text.Length;
            Segments.Add(segment);
        }
    }

    private async Task<string> EditChapterAsync(
        EditorChatContext ctx,
        Guid chapterId,
        string content,
        int? startLine,
        int? endLine)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        content ??= string.Empty;
        var existingBody = chapter.Body;
        if (ctx.ReviewEdits && ctx.EditorStaging?.TryGetChapterBodyDraft(chapter.Id, out var draftBody) == true)
            existingBody = draftBody;

        var existingLines = ChapterFormatting.SplitLines(existingBody);
        var contentLines = ChapterFormatting.SplitLines(content);

        string newBody;
        string summary;

        string InsertBeforeLine(int insertLine)
        {
            var merged = new List<string>(existingLines.Count + contentLines.Count);
            merged.AddRange(existingLines.Take(insertLine - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(insertLine - 1));
            return ChapterFormatting.JoinLines(merged);
        }

        string InsertSummary(int insertLine) => insertLine == existingLines.Count + 1
            ? existingLines.Count == 0
                ? $"Inserted {contentLines.Count} line(s) into the empty chapter."
                : $"Appended {contentLines.Count} line(s) after line {existingLines.Count}."
            : $"Inserted {contentLines.Count} line(s) before line {insertLine}.";

        if (startLine is null && endLine is null)
        {
            var appendLine = existingLines.Count + 1;
            newBody = InsertBeforeLine(appendLine);
            summary = existingLines.Count == 0
                ? $"Appended {contentLines.Count} line(s) to the empty chapter."
                : $"Appended {contentLines.Count} line(s) after line {existingLines.Count}.";
        }
        else if (startLine is int insertLine && endLine is null)
        {
            if (insertLine < 1 || insertLine > existingLines.Count + 1)
                return $"Error: startLine {insertLine} out of range (1..{existingLines.Count + 1}).";

            newBody = InsertBeforeLine(insertLine);
            summary = InsertSummary(insertLine);
        }
        else if (startLine is int replaceStart && endLine is int replaceEnd)
        {
            if (replaceStart < 1 || replaceStart > existingLines.Count)
                return $"Error: startLine {replaceStart} out of range (1..{existingLines.Count}).";
            if (replaceEnd < replaceStart || replaceEnd > existingLines.Count)
                return $"Error: endLine {replaceEnd} out of range ({replaceStart}..{existingLines.Count}).";

            var replacedCount = replaceEnd - replaceStart + 1;
            var merged = new List<string>(existingLines.Count - replacedCount + contentLines.Count);
            merged.AddRange(existingLines.Take(replaceStart - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(replaceEnd));
            newBody = ChapterFormatting.JoinLines(merged);
            summary = replaceStart == 1 && replaceEnd == existingLines.Count
                ? $"Full rewrite ({existingLines.Count} -> {contentLines.Count} lines)."
                : $"Replaced lines {replaceStart}-{replaceEnd} ({replacedCount} -> {contentLines.Count} lines).";
        }
        else
        {
            return "Error: endLine provided without startLine.";
        }

        var newNumbered = ChapterFormatting.WithLineNumbers(newBody);
        var result = $"OK. {summary}\n\nNew body:\n{(newNumbered.Length == 0 ? "(empty)" : newNumbered)}";

        if (ctx.ReviewEdits && ctx.EditorStaging is not null)
        {
            await ctx.EditorStaging.StageChapterBodyEditAsync(chapter, existingBody, newBody, summary, result);
            return result;
        }

        await chapters.UpdateAsync(chapterId, body: newBody);
        ctx.OnMutated();
        return result;
    }

    private Task<string> StartContestAsync(
        EditorChatContext ctx,
        Guid chapterId)
    {
        if (chapterId == Guid.Empty)
            return Task.FromResult("Error: chapterId is required.");

        ctx.RequestContest(new EditorContestStartRequest(chapterId));

        return Task.FromResult("Contest started. Candidate responses will stream into the review modal.");
    }

    private static string[] SearchTerms(string query) =>
        query.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim('"', '\'', '`', '(', ')', '[', ']', '{', '}', '.', ':'))
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int SearchScore(StoryEntity entity, string query, IReadOnlyList<string> searchTerms)
    {
        var score = TextMatchScore(entity.Name, query, titleWeight: 80, detailWeight: 30);
        score += TextMatchScore(entity.Type, query, titleWeight: 12, detailWeight: 8);
        foreach (var property in entity.Properties)
        {
            score += TextMatchScore(property.Key, query, titleWeight: 8, detailWeight: 4);
            score += TextMatchScore(property.Value, query, titleWeight: 8, detailWeight: 4);
        }

        foreach (var term in searchTerms)
        {
            score += TextMatchScore(entity.Name, term, titleWeight: 180, detailWeight: 60);
            score += TextMatchScore(entity.Type, term, titleWeight: 16, detailWeight: 8);
            foreach (var property in entity.Properties)
            {
                score += TextMatchScore(property.Key, term, titleWeight: 10, detailWeight: 5);
                score += TextMatchScore(property.Value, term, titleWeight: 10, detailWeight: 5);
            }
        }

        return score;
    }

    private static int TextMatchScore(string? value, string query, int titleWeight, int detailWeight)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query)) return 0;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 4;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 2;
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? detailWeight : 0;
    }

    private static object CompactEntitySearchPayload(StoryEntity entity, int score) => new
    {
        id = entity.Id,
        type = entity.Type,
        name = entity.Name,
        order = entity.Order,
        parentId = entity.ParentId,
        matchScore = score,
        properties = CompactProperties(entity.Properties),
    };

    private static Dictionary<string, string?> CompactProperties(IReadOnlyDictionary<string, string?> properties)
    {
        var compact = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties
            .Where(property => !string.Equals(property.Key, "order", StringComparison.OrdinalIgnoreCase))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        {
            compact[property.Key] = TruncatePropertyValue(property.Value);
        }

        return compact;
    }

    private static string? TruncatePropertyValue(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 240 ? value : value[..240] + "...";

    private static bool IsSearchableEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase);
}

public enum EditorChatToolMode
{
    Normal,
    ContestPreparation,
}

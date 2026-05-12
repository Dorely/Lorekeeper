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
    OutlineCollaborationTools outlineTools)
{
    public IList<AITool> Build(EditorChatContext context, EditorChatToolMode mode = EditorChatToolMode.Normal)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: () => ListContextAsync(context),
                name: "list_context",
                description: "Read the currently assembled editor context exactly as the model sees it, including enabled outline, facts, writing samples, and selected entities."),

            AIFunctionFactory.Create(
                method: (string query, int topK) => VectorSearchAsync(context, query, topK),
                name: "vector_search",
                description: "Semantic search over indexed chapters and lore for the current project. Returns the top matching snippets with their source metadata."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List every chapter in the current project (id, order, title, synopsis)."),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(context),
                name: "list_project_facts",
                description: "List project-level facts and their linked graph entities as JSON."),

            AIFunctionFactory.Create(
                method: (string query, int topK) => SearchEntitiesAsync(context, query, topK),
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
                method: (Guid entityId, int depth) => GraphNeighborsAsync(context, entityId, depth),
                name: "graph_neighbors",
                description: "Traverse graph neighbors from an entity for 1-3 degrees and return reached entities plus the edge used to reach each one."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => ReadChapterAsync(context, chapterId),
                name: "read_chapter",
                description: "Read a chapter's current body with line numbers (0001: ...). Use list_chapters to discover ids. If this turn already staged an edit to the chapter, returns the latest staged body for this turn."),
        };

        if (mode == EditorChatToolMode.ContestPreparation)
        {
            tools.Add(AIFunctionFactory.Create(
                method: (Guid chapterId) => StartContestAsync(context, chapterId),
                name: "start_contest",
                description:
                    "Start a terminal Contest Mode generation job for chapter-body mutations. " +
                    "Call this exactly once after gathering enough read-only context. " +
                    "Do not summarize, rephrase, or decide mutation instructions for the candidates; the backend snapshots the full current chat context for them."));
            return tools;
        }

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, string content, int? startLine, int? endLine) =>
                EditChapterAsync(context, chapterId, content, startLine, endLine),
            name: "edit_chapter",
            description:
                "Edit a chapter using line-based semantics. " +
                "If both startLine and endLine are null: append `content` to the end of the chapter. " +
                "If only startLine is provided: insert `content` BEFORE that line (1-based). " +
                "If both startLine and endLine are provided: replace the inclusive range of existing numbered lines with `content`; use startLine=1 and endLine=last numbered line to rewrite the full body. " +
                "Lines are 1-based and match the numbering shown by read_chapter and the editor gutter. " +
                "After editing, call read_chapter to verify the current body before finalizing. " +
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
            sb.Append(chapter.Order + 1).Append(". ").Append(chapter.Title)
              .Append(" - id=").Append(chapter.Id);
            if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
                sb.Append(" - ").Append(chapter.Synopsis);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListProjectFactsAsync(EditorChatContext ctx)
    {
        var facts = await projectFacts.ListAsync(ctx.ProjectId);
        return JsonSerializer.Serialize(facts.Select(fact => new
        {
            fact.Id,
            fact.Key,
            fact.Name,
            fact.Value,
            linkedEntities = fact.LinkedEntities.Select(link => new
            {
                link.EdgeType,
                direction = link.Direction.ToString(),
                link.EntityId,
                link.EntityName,
                link.EntityType,
            }),
        }));
    }

    private async Task<string> SearchEntitiesAsync(EditorChatContext ctx, string query, int topK)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 50);

        var matches = new List<StoryEntity>();
        var types = await entityTypes.ListAsync(ctx.ProjectId, includeStructural: true);
        foreach (var type in types.Where(type => IsSearchableEntityType(type.Type)))
        {
            var list = await entities.ListAsync(ctx.ProjectId, type.Type);
            matches.AddRange(list.Where(entity => Matches(entity, query)));
        }

        return JsonSerializer.Serialize(matches
            .OrderBy(entity => entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select(entity => new
            {
                id = entity.Id,
                type = entity.Type,
                name = entity.Name,
                parentId = entity.ParentId,
                properties = entity.Properties,
            }));
    }

    private async Task<string> ReadEntityAsync(EditorChatContext ctx, Guid entityId)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
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
        });
    }

    private async Task<string> GraphNeighborsAsync(EditorChatContext ctx, Guid entityId, int depth)
    {
        depth = Math.Clamp(depth, 1, 3);
        var start = await entities.GetAsync(ctx.ProjectId, entityId);
        if (start is null)
            return $"Error: entity {entityId} not found in this project.";

        var visited = new HashSet<Guid> { entityId };
        var frontier = new List<Guid> { entityId };
        var rows = new List<object>();

        for (var level = 1; level <= depth; level++)
        {
            var next = new List<Guid>();
            foreach (var current in frontier)
            {
                var links = await entities.ListLinksAsync(ctx.ProjectId, current);
                foreach (var link in links)
                {
                    if (!visited.Add(link.OtherEntityId)) continue;
                    var entity = await entities.GetAsync(ctx.ProjectId, link.OtherEntityId);
                    if (entity is null) continue;

                    rows.Add(new
                    {
                        depth = level,
                        via = new
                        {
                            from = current,
                            edgeType = link.EdgeType,
                            direction = link.Direction.ToString(),
                        },
                        entity = new
                        {
                            id = entity.Id,
                            type = entity.Type,
                            name = entity.Name,
                            properties = entity.Properties,
                        },
                    });
                    next.Add(entity.Id);
                }
            }

            frontier = next;
            if (frontier.Count == 0) break;
        }

        return rows.Count == 0 ? "No neighbors." : JsonSerializer.Serialize(rows);
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

    private async Task<string> ReadChapterAsync(EditorChatContext ctx, Guid chapterId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var body = chapter.Body;
        if (ctx.ReviewEdits && ctx.EditorStaging?.TryGetChapterBodyDraft(chapter.Id, out var draftBody) == true)
            body = draftBody;

        var numbered = ChapterFormatting.WithLineNumbers(body);
        return $"# {chapter.Title}\n\n{(numbered.Length == 0 ? "(empty)" : numbered)}";
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

    private static bool Matches(StoryEntity entity, string query) =>
        entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entity.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entity.Properties.Any(property =>
            property.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (property.Value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

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

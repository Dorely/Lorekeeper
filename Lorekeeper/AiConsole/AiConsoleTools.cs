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

namespace Lorekeeper.AiConsole;

/// <summary>
/// Builds the set of <see cref="AIFunction"/>s exposed to the LLM during a single
/// AI Console turn. Each tool closure captures the per-request <see cref="AiConsoleContext"/>,
/// keeping all behavior project-scoped without ambient state.
/// </summary>
public sealed class AiConsoleTools(
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
    public IList<AITool> Build(AiConsoleContext context)
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
                description: "Read a chapter's full body with line numbers (0001: ...). Use list_chapters to discover ids."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string content, int? startLine, int? endLine) =>
                    EditChapterAsync(context, chapterId, content, startLine, endLine),
                name: "edit_chapter",
                description:
                    "Edit a chapter using line-based semantics. " +
                    "If both startLine and endLine are null: replace the entire body with `content`. " +
                    "If only startLine is provided: insert `content` BEFORE that line (1-based). " +
                    "If both startLine and endLine are provided: replace the inclusive line range with `content`. " +
                    "Lines are 1-based and match the numbering shown by read_chapter and the editor gutter. " +
                    "When Review edits is enabled this stages the edit for approval instead of applying it immediately. " +
                    "Returns the new line-numbered body and a short change summary."),
        };

        var existingNames = tools.OfType<AIFunction>().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var outlineTool in outlineTools.Build(new OutlineCollaborationContext(context.ProjectId, () => { }, context.OutlineStaging)))
        {
            if (outlineTool is AIFunction function && existingNames.Add(function.Name))
                tools.Add(outlineTool);
        }

        return tools;
    }

    private async Task<string> ListContextAsync(AiConsoleContext ctx)
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
        AiConsoleContext ctx,
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
            var r = results[i];
            sb.Append('[').Append(i + 1).Append("] ")
              .Append(r.SourceType).Append('/').Append(r.SourceId ?? "?")
              .Append(" row=").Append(r.RowId);
            if (r.ChunkIndex is not null)
                sb.Append(" fragment=").Append(r.ChunkIndex.Value + 1);
            if (!string.IsNullOrWhiteSpace(r.Metadata))
                sb.Append(" - ").Append(r.Metadata);
            sb.Append(" (distance ").Append(r.Distance.ToString("F4")).Append(")\n");
            sb.Append(r.Content).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListChaptersAsync(AiConsoleContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var sb = new StringBuilder();
        foreach (var c in list)
        {
            sb.Append(c.Order + 1).Append(". ").Append(c.Title)
              .Append(" — id=").Append(c.Id);
            if (!string.IsNullOrWhiteSpace(c.Synopsis))
                sb.Append(" — ").Append(c.Synopsis);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ListProjectFactsAsync(AiConsoleContext ctx)
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

    private async Task<string> SearchEntitiesAsync(AiConsoleContext ctx, string query, int topK)
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

    private async Task<string> ReadEntityAsync(AiConsoleContext ctx, Guid entityId)
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

    private async Task<string> GraphNeighborsAsync(AiConsoleContext ctx, Guid entityId, int depth)
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

    private async Task<string> ListEntityTypesAsync(AiConsoleContext ctx)
    {
        var list = await entityTypes.ListAsync(ctx.ProjectId, includeStructural: true);
        return JsonSerializer.Serialize(list.Select(t => new
        {
            type = t.Type,
            singular = t.SingularLabel,
            plural = t.PluralLabel,
            isStructural = t.IsStructural,
            isChapterScoped = t.IsChapterScoped,
            defaultProperties = t.DefaultProperties,
        }));
    }

    private async Task<string> ListEntitiesAsync(AiConsoleContext ctx, string type, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var parsed))
                return $"Error: parentId '{parentId}' is not a valid Guid.";
            parent = parsed;
        }

        var list = await entities.ListAsync(ctx.ProjectId, type.Trim(), parent);
        return JsonSerializer.Serialize(list.Select(e => new
        {
            id = e.Id,
            type = e.Type,
            name = e.Name,
            order = e.Order,
            parentId = e.ParentId,
            properties = e.Properties,
        }));
    }

    private async Task<string> ListEntityLinksAsync(AiConsoleContext ctx, Guid entityId)
    {
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        return JsonSerializer.Serialize(links.Select(l => new
        {
            edgeId = l.EdgeId,
            edgeType = l.EdgeType,
            direction = l.Direction.ToString(),
            otherEntityId = l.OtherEntityId,
            otherEntityName = l.OtherEntityName,
            otherEntityType = l.OtherEntityType,
            sortOrder = l.SortOrder,
            properties = l.Properties,
        }));
    }

    private async Task<string> ReadChapterAsync(AiConsoleContext ctx, Guid chapterId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var numbered = ChapterFormatting.WithLineNumbers(chapter.Body);
        return $"# {chapter.Title}\n\n{(numbered.Length == 0 ? "(empty)" : numbered)}";
    }

    private async Task<string> EditChapterAsync(
        AiConsoleContext ctx,
        Guid chapterId,
        string content,
        int? startLine,
        int? endLine)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        content ??= string.Empty;
        var existingLines = ChapterFormatting.SplitLines(chapter.Body);
        var contentLines = ChapterFormatting.SplitLines(content);

        string newBody;
        string summary;

        if (startLine is null && endLine is null)
        {
            newBody = content;
            summary = $"Full overwrite ({existingLines.Count} → {contentLines.Count} lines).";
        }
        else if (startLine is int s && endLine is null)
        {
            // Insert before startLine. startLine == existingLines.Count + 1 means append.
            if (s < 1 || s > existingLines.Count + 1)
                return $"Error: startLine {s} out of range (1..{existingLines.Count + 1}).";

            var merged = new List<string>(existingLines.Count + contentLines.Count);
            merged.AddRange(existingLines.Take(s - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(s - 1));
            newBody = ChapterFormatting.JoinLines(merged);
            summary = $"Inserted {contentLines.Count} line(s) before line {s}.";
        }
        else if (startLine is int s2 && endLine is int e)
        {
            if (s2 < 1 || s2 > existingLines.Count)
                return $"Error: startLine {s2} out of range (1..{existingLines.Count}).";
            if (e < s2 || e > existingLines.Count)
                return $"Error: endLine {e} out of range ({s2}..{existingLines.Count}).";

            var replacedCount = e - s2 + 1;
            var merged = new List<string>(existingLines.Count - replacedCount + contentLines.Count);
            merged.AddRange(existingLines.Take(s2 - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(e));
            newBody = ChapterFormatting.JoinLines(merged);
            summary = $"Replaced lines {s2}-{e} ({replacedCount} → {contentLines.Count} lines).";
        }
        else
        {
            return "Error: endLine provided without startLine.";
        }

        var newNumbered = ChapterFormatting.WithLineNumbers(newBody);
        var result = $"OK. {summary}\n\nNew body:\n{(newNumbered.Length == 0 ? "(empty)" : newNumbered)}";

        if (ctx.ReviewEdits && ctx.ConsoleStaging is not null)
        {
            await ctx.ConsoleStaging.StageChapterBodyEditAsync(chapter, newBody, summary, result);
            return $"Staged for review. {summary}\n\nProposed body:\n{(newNumbered.Length == 0 ? "(empty)" : newNumbered)}";
        }

        await chapters.UpdateAsync(chapterId, body: newBody);
        return result;
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

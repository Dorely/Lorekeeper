using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Outline;

/// <summary>
/// Per-turn context captured by every Outline collaboration tool. <see cref="OnMutated"/>
/// is invoked after every successful mutating tool call so the streaming service can emit
/// an <see cref="OutlineMutated"/> event to refresh the live tree in the UI.
/// </summary>
public sealed record OutlineCollaborationContext(Guid ProjectId, Action OnMutated, OutlineToolStagingContext? Staging = null);

/// <summary>
/// Builds the set of <see cref="AITool"/>s exposed to the LLM during an Outline
/// collaboration turn. Every tool closure captures the
/// per-request <see cref="OutlineCollaborationContext"/> so behavior stays project-scoped
/// without ambient state.
/// </summary>
public sealed class OutlineCollaborationTools(
    IActService acts,
    IChapterService chapters,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IProjectFactService projectFacts,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    IAiChangeRepository changes,
    IProjectRepository projectRepository,
    IEntityRelationContextService entityRelations)
{
    private const string UnassignedSentinel = "unassigned";
    /// <summary>Canonical entity type for chapter-scoped beats.</summary>
    private const string EventNodeType = "Event";

    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    public OutlineToolStagingContext CreateStagingContext(
        Guid projectId,
        Guid conversationId,
        AiChangeConversationKind conversationKind = AiChangeConversationKind.Outline,
        Action? onDirectMutationApplied = null) =>
        new(projectId, conversationId, conversationKind, changes, projectRepository, acts, chapters, entities, entityTypes, onDirectMutationApplied);

    public async Task<IList<AITool>> BuildAsync(
        OutlineCollaborationContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: () => ListOutlineAsync(context),
                name: "list_outline",
                description: "Read the outline as structured JSON with ids, ordering, projectFacts, chapter beat counts, and staged changes when Review edits is enabled. The outline text is already in the editor Context Feed; use this for mutations, staged-state verification, or missing/insufficient feed context."),

            AIFunctionFactory.Create(
                method: (string title, string synopsis) => CreateActAsync(context, title, synopsis),
                name: "create_act",
                description: "Create a new act at the end of the outline. Returns the new act's id and order."),

            AIFunctionFactory.Create(
                method: (Guid actId, string? title = null, string? synopsis = null) => UpdateActAsync(context, actId, title, synopsis),
                name: "update_act",
                description: "Update an act's title and/or synopsis. Pass null to leave a field unchanged."),

            AIFunctionFactory.Create(
                method: (Guid actId) => DeleteActAsync(context, actId),
                name: "delete_act",
                description: "Delete an act. Any chapters it owned move to the project's unassigned bucket."),

            AIFunctionFactory.Create(
                method: (string title, string synopsis, string? actId = null) => CreateChapterAsync(context, actId, title, synopsis),
                name: "create_chapter",
                description: "Create a chapter. Pass actId as the act's Guid to place it in that act, or omit/null/'unassigned' to land in the unassigned bucket. Order is auto-assigned to the end of the chosen bucket."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string? title = null, string? synopsis = null, string? actId = null) => UpdateChapterAsync(context, chapterId, title, synopsis, actId),
                name: "update_chapter",
                description: "Update a chapter's title/synopsis and/or move it between act buckets. Pass null to leave a field unchanged. For actId: omit/null = leave act unchanged; 'unassigned' = move to unassigned; or pass a Guid to move into that act."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? startLine = null, int? endLine = null) => ReadChapterAsync(context, chapterId, startLine, endLine),
                name: "read_chapter",
                description: "Read a chapter's persisted body with line numbers (0001: ...). Use chapter ids from the current outline context when available; otherwise use list_outline to discover chapter ids. Pass optional startLine/endLine to read only part of a long chapter; omit both to read the full body."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => DeleteChapterAsync(context, chapterId),
                name: "delete_chapter",
                description: "Delete a chapter."),

            AIFunctionFactory.Create(
                method: (Guid[] orderedIds) => ReorderActsAsync(context, orderedIds),
                name: "reorder_acts",
                description: "Replace the act ordering with the given sequence of act ids. Any acts not in the list keep their relative order at the end."),

            AIFunctionFactory.Create(
                method: (Guid[] orderedIds, string? actId = null) => ReorderChaptersAsync(context, actId, orderedIds),
                name: "reorder_chapters",
                description: "Replace chapter ordering within a single act bucket. Pass actId as a Guid for that act, or omit/null/'unassigned' for the unassigned bucket."),

            // ---- generic entity tools (Characters, Locations, Events/beats, ...) ----

            AIFunctionFactory.Create(
                method: () => ListEntityTypesAsync(context),
                name: "list_entity_types",
                description: "List registered and discovered graph entity types for this project, including structural types such as Project, Act, Chapter, and Event/Beat."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 10, string? type = null, string? parentId = null) => SearchEntitiesAsync(context, query, topK, type, parentId),
                name: "search_entities",
                description: "Bounded search for graph entities by name, type, property text, aliases, and wiki text. Use type or parentId to narrow results when known. When Review edits is enabled, returns staged state. Returns compact matches; call read_entity or list_entity_links for details."),

            AIFunctionFactory.Create(
                method: (string type, string name, string? propertiesJson = null, string? parentId = null, int? order = null) =>
                    CreateEntityAsync(context, type, name, propertiesJson, parentId, order),
                name: "create_entity",
                description: "Create a new graph entity. type is the entity category ('Character', 'Location', 'Event' for beats, 'ProjectFact' for rare project-level guidance, ...). name is the display name. propertiesJson is a JSON object string for the free-form property bag (e.g. '{\"description\":\"...\", \"role\":\"...\"}') or null/empty for none. Use ProjectFact only for premise, genre, tone, theme, scope, global rules, or other guidance with no better structural home; do not use it for rework notes, act/chapter plans, character roles, beats, relationships, or location details. For ProjectFact include key/value properties; it will be parented to the Project automatically. For chapter-scoped beats set type='Event' and parentId=<chapter id> (order is auto-assigned to the end if omitted). If an entity with the same name/key already exists, returns status='existing_match' and the existing id instead of creating a duplicate. Returns the created or staged entity payload."),

            AIFunctionFactory.Create(
                method: (string entityId, string? name = null, string? propertiesToSetJson = null, string? propertiesToRemoveJson = null) =>
                    UpdateEntityAsync(context, entityId, name, propertiesToSetJson, propertiesToRemoveJson),
                name: "update_entity",
                description: "Update an entity's name and/or properties. Pass null for fields to leave unchanged. propertiesToSetJson is a JSON object string of keys to merge into the existing bag (e.g. '{\"role\":\"protagonist\"}'). propertiesToRemoveJson is a JSON array string of keys to delete (e.g. '[\"role\"]'). Returns the updated or staged entity payload."),

            AIFunctionFactory.Create(
                method: (string entityId) => DeleteEntityAsync(context, entityId),
                name: "delete_entity",
                description: "Delete an entity and any edges connected to it. Returns a deleted status plus the deleted entity snapshot."),

            AIFunctionFactory.Create(
                method: (string type, string parentId, string orderedIdsJson) =>
                    ReorderEntitiesAsync(context, type, parentId, orderedIdsJson),
                name: "reorder_entities",
                description: "Replace the ordering of a parent's children of a given type. orderedIdsJson is a JSON array string of entity ids (e.g. '[\"<guid1>\", \"<guid2>\"]') and must contain exactly the parent's current children of that type. Used primarily to reorder beats within a chapter. Returns ordered child payloads."),

            AIFunctionFactory.Create(
                method: (string fromId, string toId, string edgeType, string? propertiesJson = null) =>
                    LinkEntitiesAsync(context, fromId, toId, edgeType, propertiesJson),
                name: "link_entities",
                description: "Create a typed edge between two entities. propertiesJson is an optional JSON object string of edge metadata. Conventional edge types: 'AppearsIn' (Character -> Event/Chapter), 'LocatedAt' (Event -> Location), 'KnownTo' (Character -> Character). Other types are allowed; use camel-case verbs. Returns link details plus updated source and target payloads."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 8) => VectorSearchAsync(context, query, topK),
                name: "vector_search",
                description: "Semantic search over indexed lore and chapters in the current project. Likely returns nothing during early outline work — that just means no lore has been indexed yet."),
        };

        if (!await embeddings.IsAvailableAsync(cancellationToken))
            tools.RemoveAll(tool => tool is AIFunction function && string.Equals(function.Name, "vector_search", StringComparison.Ordinal));

        return tools;
    }

    // ---- list ------------------------------------------------------------

    private async Task<string> ListOutlineAsync(OutlineCollaborationContext ctx)
    {
        if (ctx.Staging is not null)
            return await ctx.Staging.ListOutlineAsync();

        var actList = await acts.ListAsync(ctx.ProjectId);
        var allChapters = await chapters.ListAsync(ctx.ProjectId);
        var byAct = allChapters.Where(c => c.ActId is not null)
                               .GroupBy(c => c.ActId!.Value)
                               .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order).ToList());
        var unassigned = allChapters.Where(c => c.ActId is null).OrderBy(c => c.Order).ToList();

        // Pre-resolve beat counts per chapter so the assistant can decide whether it needs to
        // search focused beat/entity details; cheap because CountChildrenAsync short-circuits when the chapter
        // has no graph node yet.
        var beatCounts = new Dictionary<Guid, int>();
        foreach (var c in allChapters)
            beatCounts[c.Id] = await entities.CountChildrenAsync(ctx.ProjectId, c.Id, EventNodeType);
        var facts = await projectFacts.ListAsync(ctx.ProjectId);
        var factPayloads = new List<object>();
        foreach (var fact in facts)
            factPayloads.Add(await ProjectFactPayloadAsync(ctx.ProjectId, fact));

        object ProjectChapter(Chapter c) => new
        {
            id = c.Id,
            order = c.Order,
            title = c.Title,
            synopsis = c.Synopsis,
            beatCount = beatCounts.TryGetValue(c.Id, out var n) ? n : 0,
        };

        var payload = new
        {
            projectFacts = factPayloads,
            acts = actList.Select(a => new
            {
                id = a.Id,
                order = a.Order,
                title = a.Title,
                synopsis = a.Synopsis,
                chapters = (byAct.TryGetValue(a.Id, out var list) ? list : []).Select(ProjectChapter),
            }),
            unassigned = unassigned.Select(ProjectChapter),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    // ---- act mutations ---------------------------------------------------

    private async Task<string> CreateActAsync(
        OutlineCollaborationContext ctx,
        [Description("Short act title (≤ 8 words).")] string title,
        [Description("1–2 sentence summary of what this act covers.")] string synopsis)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";
        if (ctx.Staging is not null)
            return await ctx.Staging.CreateActAsync(title, synopsis);

        var act = await acts.CreateAsync(ctx.ProjectId, title.Trim(), synopsis?.Trim());
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = act.Id, order = act.Order, title = act.Title, synopsis = act.Synopsis });
    }

    private async Task<string> UpdateActAsync(
        OutlineCollaborationContext ctx,
        Guid actId,
        string? title,
        string? synopsis)
    {
        if (ctx.Staging is not null)
            return await ctx.Staging.UpdateActAsync(actId, title, synopsis);

        var existing = await acts.GetAsync(actId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: act {actId} not found in this project.";

        var updated = await acts.UpdateAsync(actId, title?.Trim(), synopsis?.Trim());
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = updated.Id, title = updated.Title, synopsis = updated.Synopsis });
    }

    private async Task<string> DeleteActAsync(OutlineCollaborationContext ctx, Guid actId)
    {
        if (ctx.Staging is not null)
            return await ctx.Staging.DeleteActAsync(actId);

        var existing = await acts.GetAsync(actId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: act {actId} not found in this project.";

        await acts.DeleteAsync(actId);
        ctx.OnMutated();
        return $"Deleted act {actId}. Owned chapters were moved to the unassigned bucket.";
    }

    // ---- chapter mutations -----------------------------------------------

    private async Task<string> CreateChapterAsync(
        OutlineCollaborationContext ctx,
        string? actId,
        string title,
        string synopsis)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";
        var (resolvedActId, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
        if (error is not null) return error;

        if (ctx.Staging is not null)
            return await ctx.Staging.CreateChapterAsync(resolvedActId, title, synopsis);

        var ch = await chapters.CreateAsync(ctx.ProjectId, resolvedActId, title.Trim(), synopsis?.Trim());
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = ch.Id, order = ch.Order, actId = ch.ActId, title = ch.Title, synopsis = ch.Synopsis });
    }

    private async Task<string> UpdateChapterAsync(
        OutlineCollaborationContext ctx,
        Guid chapterId,
        string? title,
        string? synopsis,
        string? actId)
    {
        ChapterActAssignment? assignment = null;
        if (actId is not null)
        {
            var (resolved, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
            if (error is not null) return error;
            assignment = new ChapterActAssignment(resolved);
        }

        if (ctx.Staging is not null)
            return await ctx.Staging.UpdateChapterAsync(chapterId, title, synopsis, assignment?.Value, moveChapter: actId is not null);

        var existing = await chapters.GetAsync(chapterId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var updated = await chapters.UpdateAsync(chapterId, title?.Trim(), body: null, synopsis?.Trim(), assignment);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = updated.Id, actId = updated.ActId, order = updated.Order, title = updated.Title, synopsis = updated.Synopsis });
    }

    private async Task<string> ReadChapterAsync(OutlineCollaborationContext ctx, Guid chapterId, int? startLine, int? endLine)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var rangeError = FormatLineRange(chapter.Body, startLine, endLine, out var numbered, out var rangeLabel);
        if (rangeError is not null)
            return rangeError;

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(chapter.Title);
        if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
            sb.Append("Synopsis: ").AppendLine(chapter.Synopsis.Trim());
        if (rangeLabel is not null)
            sb.Append("Range: ").AppendLine(rangeLabel);
        sb.AppendLine();
        sb.Append(numbered.Length == 0 ? "(empty)" : numbered);
        return sb.ToString();
    }

    private async Task<string> DeleteChapterAsync(OutlineCollaborationContext ctx, Guid chapterId)
    {
        if (ctx.Staging is not null)
            return await ctx.Staging.DeleteChapterAsync(chapterId);

        var existing = await chapters.GetAsync(chapterId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        await chapters.DeleteAsync(chapterId);
        ctx.OnMutated();
        return $"Deleted chapter {chapterId}.";
    }

    // ---- reorders --------------------------------------------------------

    private async Task<string> ReorderActsAsync(OutlineCollaborationContext ctx, Guid[] orderedIds)
    {
        if (orderedIds is null || orderedIds.Length == 0) return "Error: orderedIds is required.";

        if (ctx.Staging is not null)
            return await ctx.Staging.ReorderActsAsync(orderedIds);

        var existing = await acts.ListAsync(ctx.ProjectId);
        var existingIds = existing.Select(a => a.Id).ToHashSet();
        var unknown = orderedIds.Where(id => !existingIds.Contains(id)).ToList();
        if (unknown.Count > 0) return $"Error: unknown act ids: {string.Join(", ", unknown)}";

        // Append any acts the model omitted, preserving their current relative order.
        var final = orderedIds.ToList();
        foreach (var a in existing)
            if (!orderedIds.Contains(a.Id)) final.Add(a.Id);

        await acts.ReorderAsync(ctx.ProjectId, final);
        ctx.OnMutated();
        return $"Reordered {final.Count} acts.";
    }

    private async Task<string> ReorderChaptersAsync(OutlineCollaborationContext ctx, string? actId, Guid[] orderedIds)
    {
        if (orderedIds is null || orderedIds.Length == 0) return "Error: orderedIds is required.";

        var (bucket, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
        if (error is not null) return error;

        if (ctx.Staging is not null)
            return await ctx.Staging.ReorderChaptersAsync(bucket, orderedIds);

        var bucketChapters = (await chapters.ListAsync(ctx.ProjectId))
            .Where(c => c.ActId == bucket)
            .ToList();
        var bucketIds = bucketChapters.Select(c => c.Id).ToHashSet();
        var unknown = orderedIds.Where(id => !bucketIds.Contains(id)).ToList();
        if (unknown.Count > 0) return $"Error: chapter ids not in target bucket: {string.Join(", ", unknown)}";

        var final = orderedIds.ToList();
        foreach (var c in bucketChapters)
            if (!orderedIds.Contains(c.Id)) final.Add(c.Id);

        await chapters.ReorderAsync(ctx.ProjectId, bucket, final);
        ctx.OnMutated();
        return $"Reordered {final.Count} chapters in bucket {(bucket is null ? "unassigned" : bucket.ToString())}.";
    }

    // ---- search ----------------------------------------------------------

    private async Task<string> VectorSearchAsync(
        OutlineCollaborationContext ctx,
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

            if (string.Equals(r.SourceType, ContextVectorSourceTypes.Entity, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(r.SourceId)
                && Guid.TryParseExact(r.SourceId, "N", out var entityId))
            {
                var relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, entityId, EntityRelationOptions);
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

    // ---- entity tools ----------------------------------------------------

    private async Task<string> ListEntityTypesAsync(OutlineCollaborationContext ctx)
    {
        if (ctx.Staging is not null)
            return await ctx.Staging.ListEntityTypesAsync();

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

    private async Task<string> SearchEntitiesAsync(OutlineCollaborationContext ctx, string query, int topK, string? type, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var p)) return $"Error: parentId '{parentId}' is not a valid Guid.";
            parent = p;
        }

        if (ctx.Staging is not null)
            return await ctx.Staging.SearchEntitiesAsync(query, topK, type, parent);

        var searchTerms = SearchTerms(query);
        var typeNames = await SearchableTypeNamesAsync(ctx.ProjectId, type);
        var matches = new List<(StoryEntity Entity, int Score)>();
        foreach (var typeName in typeNames)
        {
            var list = await entities.ListAsync(ctx.ProjectId, typeName, parent);
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

    private async Task<IReadOnlyList<string>> SearchableTypeNamesAsync(Guid projectId, string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
            return [type.Trim()];

        var list = await entityTypes.ListAsync(projectId, includeStructural: true);
        return list
            .Where(typeDefinition => IsSearchableEntityType(typeDefinition.Type))
            .Select(typeDefinition => typeDefinition.Type)
            .ToList();
    }

    private async Task<string> CreateEntityAsync(
        OutlineCollaborationContext ctx,
        string type,
        string name,
        string? propertiesJson,
        string? parentId,
        int? order)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (string.IsNullOrWhiteSpace(name)) return "Error: name is required.";
        var trimmedType = type.Trim();

        Dictionary<string, string?>? properties;
        try { properties = ParsePropertiesJson(propertiesJson); }
        catch (Exception ex) { return $"Error: propertiesJson is not a valid JSON object: {ex.Message}"; }

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var p)) return $"Error: parentId '{parentId}' is not a valid Guid.";
            if (ctx.Staging is not null)
            {
                parent = p;
            }
            // For Event (beats) the parent must be a chapter we know about. Validate up front so
            // the chat surface gets a clear error instead of lazily upserting a phantom node.
            else if (string.Equals(type.Trim(), EventNodeType, StringComparison.OrdinalIgnoreCase))
            {
                var chapter = await chapters.GetAsync(p);
                if (chapter is null || chapter.ProjectId != ctx.ProjectId)
                    return $"Error: chapter {p} not found in this project. Beats (type='Event') require a chapter parentId.";
            }
            parent = p;
        }

        if (string.Equals(trimmedType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
        {
            parent ??= ctx.ProjectId;
            properties ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            properties.TryAdd("key", name.Trim());
            properties.TryAdd("value", string.Empty);
        }

        if (ctx.Staging is not null)
            return await ctx.Staging.CreateEntityAsync(trimmedType, name, properties, parent, order);

        var duplicate = await FindDuplicateForCreateAsync(ctx, trimmedType, name, properties, parent);
        if (duplicate is not null)
            return await DuplicateEntityResultAsync(ctx.ProjectId, trimmedType, duplicate);

        try
        {
            var created = await entities.CreateAsync(ctx.ProjectId, trimmedType, name.Trim(), properties, parent, order);
            ctx.OnMutated();
            return JsonSerializer.Serialize(await EntityPayloadAsync(ctx.ProjectId, created));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<object> ProjectFactPayloadAsync(Guid projectId, ProjectFact fact)
    {
        var linkedEntities = new List<object>();
        foreach (var link in fact.LinkedEntities)
        {
            linkedEntities.Add(new
            {
                edgeType = link.EdgeType,
                direction = link.Direction.ToString(),
                entityId = link.EntityId,
                name = link.EntityName,
                type = link.EntityType,
                relationContext = await entityRelations.BuildForEntityAsync(projectId, link.EntityId, EntityRelationOptions),
            });
        }

        return new
        {
            id = fact.Id,
            key = fact.Key,
            name = fact.Name,
            value = fact.Value,
            linkedEntities,
        };
    }

    private async Task<StoryEntity?> FindDuplicateForCreateAsync(
        OutlineCollaborationContext ctx,
        string type,
        string name,
        IReadOnlyDictionary<string, string?>? properties,
        Guid? parentId)
    {
        var candidates = parentId is not null && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
            ? await entities.ListAsync(ctx.ProjectId, type, parentId)
            : await entities.ListAsync(ctx.ProjectId, type);

        if (string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
        {
            var requestedKey = NormalizeForComparison(ReadProperty(properties, "key") ?? name);
            return candidates.FirstOrDefault(candidate =>
                NormalizeForComparison(ReadProperty(candidate.Properties, "key") ?? candidate.Name) == requestedKey);
        }

        var requestedName = NormalizeForComparison(name);
        return candidates.FirstOrDefault(candidate => NormalizeForComparison(candidate.Name) == requestedName);
    }

    private async Task<string> DuplicateEntityResultAsync(Guid projectId, string requestedType, StoryEntity duplicate) =>
        JsonSerializer.Serialize(new
        {
            status = "existing_match",
            message = $"No new {requestedType} was created because an existing {duplicate.Type} with the same name or key already exists. Use update_entity or link_entities for the existing entity, or create a more distinctly named entity if this is a separate story subject.",
            existing = await EntityPayloadAsync(projectId, duplicate),
        });

    private async Task<object> EntityPayloadAsync(Guid projectId, StoryEntity entity)
    {
        var relationContext = await entityRelations.BuildForEntityAsync(projectId, entity.Id, EntityRelationOptions);
        return new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            canonSources = entity.CanonSources,
            relationContext,
        };
    }

    private static string? ReadProperty(IReadOnlyDictionary<string, string?>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) ? value : null;

    private async Task<string> UpdateEntityAsync(
        OutlineCollaborationContext ctx,
        string entityId,
        string? name,
        string? propertiesToSetJson,
        string? propertiesToRemoveJson)
    {
        if (!Guid.TryParse(entityId, out var id)) return $"Error: entityId '{entityId}' is not a valid Guid.";

        Dictionary<string, string?>? propertiesToSet;
        try { propertiesToSet = ParsePropertiesJson(propertiesToSetJson); }
        catch (Exception ex) { return $"Error: propertiesToSetJson is not a valid JSON object: {ex.Message}"; }

        string[]? propertiesToRemove;
        try { propertiesToRemove = ParseStringArrayJson(propertiesToRemoveJson); }
        catch (Exception ex) { return $"Error: propertiesToRemoveJson is not a valid JSON array of strings: {ex.Message}"; }

        if (ctx.Staging is not null)
            return await ctx.Staging.UpdateEntityAsync(id, name, propertiesToSet, propertiesToRemove);

        try
        {
            var updated = await entities.UpdateAsync(ctx.ProjectId, id, name?.Trim(), propertiesToSet, propertiesToRemove);
            ctx.OnMutated();
            return JsonSerializer.Serialize(await EntityPayloadAsync(ctx.ProjectId, updated));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> DeleteEntityAsync(OutlineCollaborationContext ctx, string entityId)
    {
        if (!Guid.TryParse(entityId, out var id)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        if (ctx.Staging is not null)
            return await ctx.Staging.DeleteEntityAsync(id);

        try
        {
            var existing = await entities.GetAsync(ctx.ProjectId, id);
            if (existing is null)
                return $"Error: entity {id} not found in this project.";

            var deleted = await EntityPayloadAsync(ctx.ProjectId, existing);
            await entities.DeleteAsync(ctx.ProjectId, id);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "deleted",
                deleted,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ReorderEntitiesAsync(
        OutlineCollaborationContext ctx,
        string type,
        string parentId,
        string orderedIdsJson)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (!Guid.TryParse(parentId, out var parent)) return $"Error: parentId '{parentId}' is not a valid Guid.";

        string[]? orderedIds;
        try { orderedIds = ParseStringArrayJson(orderedIdsJson); }
        catch (Exception ex) { return $"Error: orderedIdsJson is not a valid JSON array of strings: {ex.Message}"; }
        if (orderedIds is null || orderedIds.Length == 0) return "Error: orderedIdsJson is required.";

        var parsed = new List<Guid>(orderedIds.Length);
        foreach (var s in orderedIds)
        {
            if (!Guid.TryParse(s, out var g)) return $"Error: orderedIdsJson contains invalid Guid '{s}'.";
            parsed.Add(g);
        }

        if (ctx.Staging is not null)
            return await ctx.Staging.ReorderEntitiesAsync(type, parent, parsed);

        try
        {
            var trimmedType = type.Trim();
            await entities.ReorderAsync(ctx.ProjectId, trimmedType, parent, parsed);
            var orderedEntities = new List<object>();
            foreach (var id in parsed)
            {
                var entity = await entities.GetAsync(ctx.ProjectId, id);
                if (entity is not null)
                    orderedEntities.Add(await EntityPayloadAsync(ctx.ProjectId, entity));
            }
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "reordered",
                type = trimmedType,
                parentId = parent,
                orderedIds = parsed,
                entities = orderedEntities,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> LinkEntitiesAsync(
        OutlineCollaborationContext ctx,
        string fromId,
        string toId,
        string edgeType,
        string? propertiesJson)
    {
        if (!Guid.TryParse(fromId, out var from)) return $"Error: fromId '{fromId}' is not a valid Guid.";
        if (!Guid.TryParse(toId, out var to)) return $"Error: toId '{toId}' is not a valid Guid.";
        if (string.IsNullOrWhiteSpace(edgeType)) return "Error: edgeType is required.";

        Dictionary<string, string?>? properties;
        try { properties = ParsePropertiesJson(propertiesJson); }
        catch (Exception ex) { return $"Error: propertiesJson is not a valid JSON object: {ex.Message}"; }

        if (ctx.Staging is not null)
            return await ctx.Staging.LinkEntitiesAsync(from, to, edgeType, properties);

        try
        {
            var trimmedEdgeType = edgeType.Trim();
            await entities.LinkAsync(ctx.ProjectId, from, to, trimmedEdgeType, properties);
            var fromEntity = await entities.GetAsync(ctx.ProjectId, from);
            var toEntity = await entities.GetAsync(ctx.ProjectId, to);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "linked",
                link = new
                {
                    fromId = from,
                    toId = to,
                    edgeType = trimmedEdgeType,
                    properties = properties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                },
                from = fromEntity is null ? null : await EntityPayloadAsync(ctx.ProjectId, fromEntity),
                to = toEntity is null ? null : await EntityPayloadAsync(ctx.ProjectId, toEntity),
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Parses a JSON object string like <c>{"k":"v","k2":null}</c> into a string?-valued dict.
    /// Returns null when the input is null/blank. Throws on malformed JSON or non-object roots.
    /// </summary>
    private static Dictionary<string, string?>? ParsePropertiesJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("expected a JSON object at the root.");
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => prop.Value.GetRawText(),
            };
        }
        return dict;
    }

    /// <summary>
    /// Parses a JSON array string of strings like <c>["a","b"]</c>. Returns null when input is
    /// null/blank. Throws on malformed JSON or non-array roots.
    /// </summary>
    private static string[]? ParseStringArrayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("expected a JSON array at the root.");
        var list = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.GetRawText());
        }
        return list.ToArray();
    }

    private static string NormalizeForComparison(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

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
        score += TextMatchScore(entity.Summary, query, titleWeight: 20, detailWeight: 12);
        foreach (var alias in entity.Aliases)
            score += TextMatchScore(alias, query, titleWeight: 30, detailWeight: 16);
        foreach (var section in entity.WikiSections)
        {
            score += TextMatchScore(section.Title, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(section.Body, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var canonSource in entity.CanonSources)
        {
            score += TextMatchScore(canonSource.SourceTitle, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(canonSource.Markdown, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var property in entity.Properties)
        {
            score += TextMatchScore(property.Key, query, titleWeight: 8, detailWeight: 4);
            score += TextMatchScore(property.Value, query, titleWeight: 8, detailWeight: 4);
        }

        foreach (var term in searchTerms)
        {
            score += TextMatchScore(entity.Name, term, titleWeight: 180, detailWeight: 60);
            score += TextMatchScore(entity.Type, term, titleWeight: 16, detailWeight: 8);
            score += TextMatchScore(entity.Summary, term, titleWeight: 28, detailWeight: 14);
            foreach (var alias in entity.Aliases)
                score += TextMatchScore(alias, term, titleWeight: 70, detailWeight: 24);
            foreach (var section in entity.WikiSections)
            {
                score += TextMatchScore(section.Title, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(section.Body, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var canonSource in entity.CanonSources)
            {
                score += TextMatchScore(canonSource.SourceTitle, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(canonSource.Markdown, term, titleWeight: 18, detailWeight: 10);
            }
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
        summary = TruncatePropertyValue(entity.Summary),
        aliases = entity.Aliases.Take(8).ToArray(),
        wikiSections = CompactWikiSections(entity.WikiSections),
        canonSources = CompactCanonSources(entity.CanonSources),
        properties = CompactProperties(entity.Properties),
    };

    private static object[] CompactCanonSources(IReadOnlyList<IngestCanonSource> sources) =>
        sources
            .Take(4)
            .Select(source => new
            {
                source.SourceTitle,
                source.SourceKind,
                markdown = TruncatePropertyValue(source.Markdown),
            })
            .ToArray();

    private static object[] CompactWikiSections(IReadOnlyList<IngestWikiSection> sections) =>
        sections
            .Take(4)
            .Select(section => new
            {
                section.Id,
                section.Title,
                body = TruncatePropertyValue(section.Body),
            })
            .ToArray();

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
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static string? FormatLineRange(
        string body,
        int? startLine,
        int? endLine,
        out string numbered,
        out string? rangeLabel)
    {
        numbered = string.Empty;
        rangeLabel = null;

        if (startLine is null && endLine is null)
        {
            numbered = ChapterFormatting.WithLineNumbers(body);
            return null;
        }

        var lines = ChapterFormatting.SplitLines(body);
        if (lines.Count == 0)
        {
            rangeLabel = "empty chapter";
            return null;
        }

        var start = startLine ?? 1;
        var end = endLine ?? lines.Count;
        if (start < 1) return "Error: startLine must be 1 or greater.";
        if (end < 1) return "Error: endLine must be 1 or greater.";
        if (start > end) return "Error: startLine must be less than or equal to endLine.";
        if (start > lines.Count) return $"Error: startLine {start} is beyond the chapter's {lines.Count} lines.";

        end = Math.Min(end, lines.Count);
        var width = Math.Max(4, lines.Count.ToString().Length);
        var sb = new StringBuilder();
        for (var i = start - 1; i < end; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(width, '0'));
            sb.Append(": ");
            sb.Append(lines[i]);
            if (i < end - 1) sb.Append('\n');
        }

        numbered = sb.ToString();
        rangeLabel = $"lines {start}-{end} of {lines.Count}";
        return null;
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// Resolves the loosely-typed <c>actId</c> string the model passes (a Guid, "unassigned",
    /// or null/empty) into a <see cref="Guid?"/>. Validates that any supplied act belongs to
    /// the current project. Returns <c>(null, errorMessage)</c> on failure.
    /// </summary>
    private async Task<(Guid? Resolved, string? Error)> ResolveActAsync(
        OutlineCollaborationContext ctx,
        string? actId,
        bool allowUnassigned)
    {
        if (string.IsNullOrWhiteSpace(actId) || string.Equals(actId, UnassignedSentinel, StringComparison.OrdinalIgnoreCase))
        {
            if (!allowUnassigned) return (null, "Error: actId is required.");
            return (null, null);
        }

        if (!Guid.TryParse(actId, out var parsed))
            return (null, $"Error: actId '{actId}' is not a valid Guid or 'unassigned'.");

        if (ctx.Staging is not null)
            return (parsed, null);

        var act = await acts.GetAsync(parsed);
        if (act is null || act.ProjectId != ctx.ProjectId)
            return (null, $"Error: act {parsed} not found in this project.");

        return (parsed, null);
    }
}

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Knowledge;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Projects;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Outline;

/// <summary>
/// Per-turn context captured by every Outline collaboration tool. <see cref="OnMutated"/>
/// is invoked after every successful mutating tool call so the streaming service can emit
/// an <see cref="OutlineMutated"/> event to refresh the live tree in the UI.
/// </summary>
public sealed record OutlineCollaborationContext(Guid ProjectId, Action OnMutated);

/// <summary>
/// Builds the set of <see cref="AITool"/>s exposed to the LLM during an Outline
/// collaboration turn. Mirrors <c>AiConsoleTools</c>: every tool closure captures the
/// per-request <see cref="OutlineCollaborationContext"/> so behavior stays project-scoped
/// without ambient state.
/// </summary>
public sealed class OutlineCollaborationTools(
    IActService acts,
    IChapterService chapters,
    IProjectService projects,
    IVectorStore vectors,
    IEmbeddingService embeddings)
{
    private const string UnassignedSentinel = "unassigned";

    public IList<AITool> Build(OutlineCollaborationContext context)
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                method: () => ListOutlineAsync(context),
                name: "list_outline",
                description: "Read the current outline as JSON: an ordered list of acts (each with id/title/synopsis and an ordered list of their chapters), plus an 'unassigned' bucket for chapters without an act."),

            AIFunctionFactory.Create(
                method: (string title, string synopsis) => CreateActAsync(context, title, synopsis),
                name: "create_act",
                description: "Create a new act at the end of the outline. Returns the new act's id and order."),

            AIFunctionFactory.Create(
                method: (Guid actId, string? title, string? synopsis) => UpdateActAsync(context, actId, title, synopsis),
                name: "update_act",
                description: "Update an act's title and/or synopsis. Pass null to leave a field unchanged."),

            AIFunctionFactory.Create(
                method: (Guid actId) => DeleteActAsync(context, actId),
                name: "delete_act",
                description: "Delete an act. Any chapters it owned move to the project's unassigned bucket."),

            AIFunctionFactory.Create(
                method: (string? actId, string title, string synopsis) => CreateChapterAsync(context, actId, title, synopsis),
                name: "create_chapter",
                description: "Create a chapter. Pass actId as the act's Guid to place it in that act, or omit/null/'unassigned' to land in the unassigned bucket. Order is auto-assigned to the end of the chosen bucket."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string? title, string? synopsis, string? actId) => UpdateChapterAsync(context, chapterId, title, synopsis, actId),
                name: "update_chapter",
                description: "Update a chapter's title/synopsis and/or move it between act buckets. Pass null to leave a field unchanged. For actId: omit/null = leave act unchanged; 'unassigned' = move to unassigned; or pass a Guid to move into that act."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => DeleteChapterAsync(context, chapterId),
                name: "delete_chapter",
                description: "Delete a chapter."),

            AIFunctionFactory.Create(
                method: (Guid[] orderedIds) => ReorderActsAsync(context, orderedIds),
                name: "reorder_acts",
                description: "Replace the act ordering with the given sequence of act ids. Any acts not in the list keep their relative order at the end."),

            AIFunctionFactory.Create(
                method: (string? actId, Guid[] orderedIds) => ReorderChaptersAsync(context, actId, orderedIds),
                name: "reorder_chapters",
                description: "Replace chapter ordering within a single act bucket. Pass actId as a Guid for that act, or omit/null/'unassigned' for the unassigned bucket."),

            AIFunctionFactory.Create(
                method: (string key, string value) => SetProjectMetadataAsync(context, key, value),
                name: "set_project_metadata",
                description: "Persist a free-form value into the project's metadata bag. Use the 'outline.' namespace for outline-related facts (e.g. outline.premise, outline.tone, outline.scope, outline.characters, outline.conflict, outline.setting). Overwrites any existing value at the same key."),

            AIFunctionFactory.Create(
                method: (string query, int topK) => VectorSearchAsync(context, query, topK),
                name: "vector_search",
                description: "Semantic search over indexed lore and chapters in the current project. Likely returns nothing during early outline work — that just means no lore has been indexed yet."),
        };
    }

    // ---- list ------------------------------------------------------------

    private async Task<string> ListOutlineAsync(OutlineCollaborationContext ctx)
    {
        var actList = await acts.ListAsync(ctx.ProjectId);
        var allChapters = await chapters.ListAsync(ctx.ProjectId);
        var byAct = allChapters.Where(c => c.ActId is not null)
                               .GroupBy(c => c.ActId!.Value)
                               .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order).ToList());
        var unassigned = allChapters.Where(c => c.ActId is null).OrderBy(c => c.Order).ToList();

        var payload = new
        {
            acts = actList.Select(a => new
            {
                id = a.Id,
                order = a.Order,
                title = a.Title,
                synopsis = a.Synopsis,
                chapters = (byAct.TryGetValue(a.Id, out var list) ? list : []).Select(c => new
                {
                    id = c.Id,
                    order = c.Order,
                    title = c.Title,
                    synopsis = c.Synopsis,
                }),
            }),
            unassigned = unassigned.Select(c => new
            {
                id = c.Id,
                order = c.Order,
                title = c.Title,
                synopsis = c.Synopsis,
            }),
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
        var existing = await acts.GetAsync(actId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: act {actId} not found in this project.";

        var updated = await acts.UpdateAsync(actId, title?.Trim(), synopsis?.Trim());
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = updated.Id, title = updated.Title, synopsis = updated.Synopsis });
    }

    private async Task<string> DeleteActAsync(OutlineCollaborationContext ctx, Guid actId)
    {
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
        var existing = await chapters.GetAsync(chapterId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        ChapterActAssignment? assignment = null;
        if (actId is not null)
        {
            var (resolved, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
            if (error is not null) return error;
            assignment = new ChapterActAssignment(resolved);
        }

        var updated = await chapters.UpdateAsync(chapterId, title?.Trim(), body: null, synopsis?.Trim(), assignment);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = updated.Id, actId = updated.ActId, order = updated.Order, title = updated.Title, synopsis = updated.Synopsis });
    }

    private async Task<string> DeleteChapterAsync(OutlineCollaborationContext ctx, Guid chapterId)
    {
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

    // ---- metadata + search -----------------------------------------------

    private async Task<string> SetProjectMetadataAsync(OutlineCollaborationContext ctx, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Error: key is required.";
        await projects.UpdateMetadataAsync(ctx.ProjectId, new Dictionary<string, object?> { [key.Trim()] = value });
        // Metadata is not part of the visible tree, but downstream context may show it; signal anyway.
        ctx.OnMutated();
        return $"Set {key.Trim()}.";
    }

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
              .Append(" (distance ").Append(r.Distance.ToString("F4")).Append(")\n");
            sb.Append(r.Content).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
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

        var act = await acts.GetAsync(parsed);
        if (act is null || act.ProjectId != ctx.ProjectId)
            return (null, $"Error: act {parsed} not found in this project.");

        return (parsed, null);
    }
}

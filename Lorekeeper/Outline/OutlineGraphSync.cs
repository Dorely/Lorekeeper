using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class OutlineGraphSync(
    IGraphStore graph,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges,
    IProjectRepository projects,
    IActRepository acts,
    IChapterRepository chapters,
    IEntityTypeService entityTypes) : IOutlineGraphSync
{
    public const string HasChildEdgeType = "HasChild";

    public async Task EnsureProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        await entityTypes.EnsureDefaultsAsync(project.Id, cancellationToken);
        await graph.UpsertNodeAsync(
            project.Id,
            EntityTypeService.ProjectNodeType,
            project.Id.ToString("N"),
            project.Name,
            new Dictionary<string, object?>
            {
                ["slug"] = project.Slug,
                ["sourceType"] = "project",
                ["sourceId"] = project.Id.ToString("N"),
                ["structural"] = true,
            },
            cancellationToken);
    }

    public async Task EnsureActAsync(Act act, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(act.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {act.ProjectId} not found.");
        await EnsureProjectAsync(project, cancellationToken);

        var projectNode = await GetRequiredNodeAsync(act.ProjectId, EntityTypeService.ProjectNodeType, project.Id, cancellationToken);
        var actNode = await graph.UpsertNodeAsync(
            act.ProjectId,
            EntityTypeService.ActNodeType,
            act.Id.ToString("N"),
            act.Title,
            new Dictionary<string, object?>
            {
                ["synopsis"] = act.Synopsis,
                ["order"] = act.Order,
                ["sourceType"] = "act",
                ["sourceId"] = act.Id.ToString("N"),
                ["structural"] = true,
            },
            cancellationToken);

        await EnsureSingleParentEdgeAsync(projectNode.Id, actNode.Id, act.Order, cancellationToken);
    }

    public async Task RemoveActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default)
    {
        var actNode = await graph.FindNodeAsync(projectId, EntityTypeService.ActNodeType, actId.ToString("N"), cancellationToken);
        if (actNode is null) return;

        await graph.RemoveNodeAsync(actNode.Id, cancellationToken);
    }

    public async Task EnsureChapterAsync(Chapter chapter, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(chapter.ProjectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {chapter.ProjectId} not found.");
        await EnsureProjectAsync(project, cancellationToken);

        GraphNode parentNode;
        if (chapter.ActId is Guid actId)
        {
            var act = await acts.GetByIdAsync(actId, cancellationToken)
                ?? throw new InvalidOperationException($"Act {actId} not found.");
            await EnsureActAsync(act, cancellationToken);
            parentNode = await GetRequiredNodeAsync(chapter.ProjectId, EntityTypeService.ActNodeType, actId, cancellationToken);
        }
        else
        {
            parentNode = await GetRequiredNodeAsync(chapter.ProjectId, EntityTypeService.ProjectNodeType, chapter.ProjectId, cancellationToken);
        }

        var chapterNode = await graph.UpsertNodeAsync(
            chapter.ProjectId,
            EntityTypeService.ChapterNodeType,
            chapter.Id.ToString("N"),
            chapter.Title,
            new Dictionary<string, object?>
            {
                ["synopsis"] = chapter.Synopsis,
                ["order"] = chapter.Order,
                ["sourceType"] = "chapter",
                ["sourceId"] = chapter.Id.ToString("N"),
                ["structural"] = true,
                ["vectorIndexState"] = chapter.VectorIndexState.ToString(),
                ["vectorIndexedAt"] = chapter.VectorIndexedAt?.ToString("o"),
                ["vectorIndexError"] = chapter.VectorIndexError,
            },
            cancellationToken);

        await EnsureSingleParentEdgeAsync(parentNode.Id, chapterNode.Id, chapter.Order, cancellationToken);
    }

    public async Task RemoveChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default)
    {
        var chapterNode = await graph.FindNodeAsync(projectId, EntityTypeService.ChapterNodeType, chapterId.ToString("N"), cancellationToken);
        if (chapterNode is null) return;

        var outgoingChildren = await edges.GetAdjacentAsync(
            chapterNode.Id,
            EdgeDirection.Outgoing,
            new[] { HasChildEdgeType },
            maxResults: null,
            cancellationToken);

        if (outgoingChildren.Count > 0)
        {
            var childNodes = await nodes.GetByIdsAsync(outgoingChildren.Select(e => e.ToNodeId), cancellationToken);
            foreach (var child in childNodes)
                await graph.RemoveNodeAsync(child.Id, cancellationToken);
        }

        await graph.RemoveNodeAsync(chapterNode.Id, cancellationToken);
    }

    public async Task RepairProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");
        await EnsureProjectAsync(project, cancellationToken);

        foreach (var act in await acts.ListByProjectAsync(projectId, cancellationToken))
            await EnsureActAsync(act, cancellationToken);

        foreach (var chapter in await chapters.ListByProjectAsync(projectId, cancellationToken))
            await EnsureChapterAsync(chapter, cancellationToken);
    }

    private async Task EnsureSingleParentEdgeAsync(
        long parentNodeId,
        long childNodeId,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        var incomingParents = await edges.GetAdjacentAsync(
            childNodeId,
            EdgeDirection.Incoming,
            new[] { HasChildEdgeType },
            maxResults: null,
            cancellationToken);

        foreach (var incoming in incomingParents.Where(e => e.FromNodeId != parentNodeId))
            await graph.RemoveEdgeAsync(incoming.Id, cancellationToken);

        await graph.UpsertEdgeAsync(
            parentNodeId,
            childNodeId,
            HasChildEdgeType,
            properties: null,
            sortOrder: sortOrder,
            cancellationToken: cancellationToken);
    }

    private async Task<GraphNode> GetRequiredNodeAsync(
        Guid projectId,
        string nodeType,
        Guid key,
        CancellationToken cancellationToken) =>
        await graph.FindNodeAsync(projectId, nodeType, key.ToString("N"), cancellationToken)
            ?? throw new InvalidOperationException($"Graph node {nodeType}/{key:N} not found in project {projectId}.");
}
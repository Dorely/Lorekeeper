using Lorekeeper.Outline;

namespace Lorekeeper.Context;

public sealed class EntityRelationContextService(IEntityService entities) : IEntityRelationContextService
{
    private static readonly EntityRelationContextOptions DefaultOptions = new();

    public async Task<EntityRelationContext> BuildForEntityAsync(
        Guid projectId,
        Guid entityId,
        EntityRelationContextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedOptions = Normalize(options);
        var root = await entities.GetAsync(projectId, entityId, cancellationToken);
        if (root is null) return EntityRelationContext.Empty;

        var links = await entities.ListLinksAsync(projectId, entityId, cancellationToken);
        var directLinks = links
            .Where(link => !link.IsAutoLink && CanTraverse(link))
            .Take(resolvedOptions.MaxDirectLinks)
            .Select(link => ProjectDirectLink(root, link))
            .ToList();
        var autoLinks = links
            .Where(link => link.IsAutoLink && CanTraverse(link))
            .Take(resolvedOptions.MaxDirectLinks)
            .Select(link => ProjectDirectLink(root, link))
            .ToList();
        var traversalMap = await BuildTraversalMapAsync(projectId, [entityId], resolvedOptions, cancellationToken);

        return new EntityRelationContext(directLinks, traversalMap, autoLinks);
    }

    public async Task<IReadOnlyList<EntityTraversalPathContext>> BuildTraversalMapAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> rootEntityIds,
        EntityRelationContextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedOptions = Normalize(options);
        if (rootEntityIds.Count == 0 || resolvedOptions.Depth <= 0 || resolvedOptions.MaxTraversalPaths <= 0)
            return [];

        var paths = new List<EntityTraversalPathContext>();
        var visited = new HashSet<Guid>();
        var queue = new Queue<TraversalCursor>();

        foreach (var rootId in rootEntityIds.Distinct())
        {
            var root = await entities.GetAsync(projectId, rootId, cancellationToken);
            if (root is null) continue;

            visited.Add(root.Id);
            queue.Enqueue(new TraversalCursor(root.Id, Depth: 0, FormatEntity(root)));
        }

        while (queue.Count > 0 && paths.Count < resolvedOptions.MaxTraversalPaths)
        {
            var current = queue.Dequeue();
            if (current.Depth >= resolvedOptions.Depth) continue;

            var links = await entities.ListLinksAsync(projectId, current.EntityId, cancellationToken);
            foreach (var link in links.Take(resolvedOptions.MaxLinksPerNode))
            {
                if (link.IsAutoLink || !CanTraverse(link) || link.OtherEntityId == Guid.Empty || !visited.Add(link.OtherEntityId))
                    continue;

                var other = await entities.GetAsync(projectId, link.OtherEntityId, cancellationToken);
                if (other is null) continue;

                var nextDepth = current.Depth + 1;
                var nextPath = AppendHop(current.Path, link, other);
                paths.Add(new EntityTraversalPathContext(nextDepth, other.Id, other.Type, other.Name, nextPath));
                if (paths.Count >= resolvedOptions.MaxTraversalPaths) break;

                queue.Enqueue(new TraversalCursor(other.Id, nextDepth, nextPath));
            }
        }

        return paths;
    }

    public string FormatTraversalMap(IReadOnlyList<EntityTraversalPathContext> traversalMap)
    {
        if (traversalMap.Count == 0) return "(no graph traversal paths found)";
        return string.Join('\n', traversalMap.Select(path => "- " + path.Path));
    }

    private static EntityRelationContextOptions Normalize(EntityRelationContextOptions? options)
    {
        options ??= DefaultOptions;
        return new EntityRelationContextOptions
        {
            Depth = Math.Clamp(options.Depth, 0, 3),
            MaxDirectLinks = Math.Clamp(options.MaxDirectLinks, 0, 30),
            MaxTraversalPaths = Math.Clamp(options.MaxTraversalPaths, 0, 80),
            MaxLinksPerNode = Math.Clamp(options.MaxLinksPerNode, 1, 30),
        };
    }

    private static EntityDirectLinkContext ProjectDirectLink(StoryEntity root, EntityLink link) => new(
        link.EdgeId,
        link.EdgeType,
        link.Direction.ToString(),
        link.OtherEntityId,
        link.OtherEntityName,
        link.OtherEntityType,
        link.SortOrder,
        link.Properties,
        AppendHop(FormatEntity(root), link),
        link.IsAutoLink);

    private static string AppendHop(string path, EntityLink link, StoryEntity other) => AppendHop(path, link, FormatEntity(other));

    private static string AppendHop(string path, EntityLink link) =>
        AppendHop(path, link, FormatEntity(link.OtherEntityType, link.OtherEntityName, link.OtherEntityId));

    private static string AppendHop(string path, EntityLink link, string other)
    {
        var relation = string.IsNullOrWhiteSpace(link.EdgeType) ? "related" : link.EdgeType;
        return link.Direction == EntityLinkDirection.Outgoing
            ? $"{path} -[{relation}]-> {other}"
            : $"{path} <-[{relation}]- {other}";
    }

    private static string FormatEntity(StoryEntity entity) => FormatEntity(entity.Type, entity.Name, entity.Id);

    private static string FormatEntity(string type, string name, Guid id)
    {
        var label = string.IsNullOrWhiteSpace(name) ? id.ToString("N") : name.Trim();
        return $"{type} \"{label}\" (id={id:N})";
    }

    private static bool CanTraverse(EntityLink link) =>
        !string.Equals(link.OtherEntityType, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(link.OtherEntityType, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(link.OtherEntityType, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private sealed record TraversalCursor(Guid EntityId, int Depth, string Path);
}

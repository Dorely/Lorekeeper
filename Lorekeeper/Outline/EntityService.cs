using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Outline;

public sealed class EntityService(
    IGraphStore graph,
    IGraphNodeRepository nodes,
    IGraphEdgeRepository edges) : IEntityService
{
    /// <summary>Canonical chapter <see cref="GraphNode.NodeType"/> for parent links.</summary>
    public const string ChapterNodeType = "Chapter";

    /// <summary>Canonical edge type linking a parent node to its ordered children.</summary>
    public const string HasChildEdgeType = "HasChild";

    public async Task<IReadOnlyList<StoryEntity>> ListAsync(
        Guid projectId,
        string nodeType,
        Guid? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (parentId is null)
        {
            var all = await nodes.ListByTypeAsync(projectId, nodeType, cancellationToken);
            return all.Select(n => Project(n, parentId: null)).ToList();
        }

        var parent = await ResolveEntityNodeAsync(projectId, parentId.Value, cancellationToken);
        if (parent is null) return [];

        var outgoing = await edges.GetAdjacentAsync(
            parent.Id,
            EdgeDirection.Outgoing,
            new[] { HasChildEdgeType },
            maxResults: null,
            cancellationToken);

        if (outgoing.Count == 0) return [];

        var childIds = outgoing.Select(e => e.ToNodeId).ToList();
        var orderByChildId = outgoing.ToDictionary(e => e.ToNodeId, e => e.SortOrder);
        var children = await nodes.GetByIdsAsync(childIds, cancellationToken);

        return children
            .Where(n => n.NodeType == nodeType)
            .OrderBy(n => orderByChildId.TryGetValue(n.Id, out var order) ? order ?? int.MaxValue : int.MaxValue)
            .ThenBy(n => n.Label ?? n.Key, StringComparer.OrdinalIgnoreCase)
            .Select(n => Project(n, parentId, orderByChildId.TryGetValue(n.Id, out var order) ? order : null))
            .ToList();
    }

    public async Task<StoryEntity> CreateAsync(
        Guid projectId,
        string nodeType,
        string name,
        IDictionary<string, string?>? properties = null,
        Guid? parentId = null,
        int? order = null,
        Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Entity name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(nodeType))
            throw new ArgumentException("Entity type is required.", nameof(nodeType));

        var key = (id ?? Guid.NewGuid()).ToString("N");
        var props = ToObjectDict(properties);

        if (parentId is not null)
        {
            // Auto-assign order to the end of the parent's child list when not specified.
            if (order is null)
            {
                var siblings = await ListAsync(projectId, nodeType, parentId, cancellationToken);
                order = siblings.Count == 0 ? 0 : siblings.Max(s => s.Order ?? -1) + 1;
            }
        }

        var node = await graph.UpsertNodeAsync(projectId, nodeType, key, name.Trim(), props, cancellationToken);

        if (parentId is not null)
        {
            var parent = await EnsureParentNodeAsync(projectId, parentId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Parent entity {parentId} not found in project {projectId}.");
            await graph.UpsertEdgeAsync(
                parent.Id,
                node.Id,
                HasChildEdgeType,
                properties: null,
                sortOrder: order,
                cancellationToken: cancellationToken);
        }

        return Project(node, parentId);
    }

    public async Task<StoryEntity> UpdateAsync(
        Guid projectId,
        Guid entityId,
        string? name = null,
        IDictionary<string, string?>? propertiesToSet = null,
        IReadOnlyCollection<string>? propertiesToRemove = null,
        CancellationToken cancellationToken = default)
    {
        var node = await ResolveEntityNodeAsync(projectId, entityId, cancellationToken)
            ?? throw new InvalidOperationException($"Entity {entityId} not found in project {projectId}.");

        // Mutate in-place through the repo so we control merge semantics (graph store's UpsertNodeAsync
        // *replaces* the property bag wholesale when a non-null dict is passed).
        if (!string.IsNullOrWhiteSpace(name))
            node.Label = name.Trim();

        if (propertiesToSet is { Count: > 0 })
        {
            foreach (var kv in propertiesToSet)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                node.Properties[kv.Key] = kv.Value;
            }
        }

        if (propertiesToRemove is { Count: > 0 })
        {
            foreach (var k in propertiesToRemove)
            {
                node.Properties.Remove(k);
            }
        }

        node.UpdatedAt = DateTime.UtcNow;
        nodes.Update(node);
        await nodes.SaveChangesAsync(cancellationToken);

        var parent = await FindParentAsync(node.Id, cancellationToken);
        return Project(node, parent);
    }

    public async Task DeleteAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default)
    {
        var node = await ResolveEntityNodeAsync(projectId, entityId, cancellationToken);
        if (node is null) return;
        await graph.RemoveNodeAsync(node.Id, cancellationToken);
    }

    public async Task ReorderAsync(
        Guid projectId,
        string nodeType,
        Guid parentId,
        IReadOnlyList<Guid> orderedEntityIds,
        CancellationToken cancellationToken = default)
    {
        var parent = await ResolveEntityNodeAsync(projectId, parentId, cancellationToken)
            ?? throw new InvalidOperationException($"Parent entity {parentId} not found in project {projectId}.");

        var outgoing = await edges.GetAdjacentAsync(
            parent.Id,
            EdgeDirection.Outgoing,
            new[] { HasChildEdgeType },
            maxResults: null,
            cancellationToken);
        var children = await nodes.GetByIdsAsync(outgoing.Select(e => e.ToNodeId).ToList(), cancellationToken);
        var matchingChildren = children.Where(n => n.NodeType == nodeType).ToDictionary(n => n.Key);
        var edgeByChildId = outgoing.ToDictionary(e => e.ToNodeId);

        // Validate the requested ordering covers exactly the parent's children of this type.
        var requestedKeys = orderedEntityIds.Select(g => g.ToString("N")).ToHashSet(StringComparer.Ordinal);
        if (requestedKeys.Count != orderedEntityIds.Count)
            throw new InvalidOperationException("Reorder list contains duplicate ids.");
        if (!requestedKeys.SetEquals(matchingChildren.Keys))
            throw new InvalidOperationException(
                "Reorder list must contain exactly the parent's children of the given type.");

        for (var i = 0; i < orderedEntityIds.Count; i++)
        {
            var child = matchingChildren[orderedEntityIds[i].ToString("N")];
            if (!edgeByChildId.TryGetValue(child.Id, out var edge)) continue;
            edge.SortOrder = i;
            edge.UpdatedAt = DateTime.UtcNow;
            edges.Update(edge);
        }

        await edges.SaveChangesAsync(cancellationToken);
    }

    public async Task LinkAsync(
        Guid projectId,
        Guid fromEntityId,
        Guid toEntityId,
        string edgeType,
        IDictionary<string, string?>? properties = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(edgeType))
            throw new ArgumentException("Edge type is required.", nameof(edgeType));
        if (fromEntityId == toEntityId)
            throw new InvalidOperationException("Cannot link an entity to itself.");

        var fromNode = await ResolveEntityNodeAsync(projectId, fromEntityId, cancellationToken)
            ?? throw new InvalidOperationException($"Source entity {fromEntityId} not found in project {projectId}.");
        var toNode = await ResolveEntityNodeAsync(projectId, toEntityId, cancellationToken)
            ?? throw new InvalidOperationException($"Target entity {toEntityId} not found in project {projectId}.");

        await graph.UpsertEdgeAsync(
            fromNode.Id,
            toNode.Id,
            edgeType.Trim(),
            properties: ToObjectDict(properties),
            cancellationToken: cancellationToken);
    }

    public async Task<int> CountChildrenAsync(
        Guid projectId,
        Guid parentId,
        string childNodeType,
        CancellationToken cancellationToken = default)
    {
        var parent = await ResolveEntityNodeAsync(projectId, parentId, cancellationToken);
        if (parent is null) return 0;

        var outgoing = await edges.GetAdjacentAsync(
            parent.Id,
            EdgeDirection.Outgoing,
            new[] { HasChildEdgeType },
            maxResults: null,
            cancellationToken);
        if (outgoing.Count == 0) return 0;

        var children = await nodes.GetByIdsAsync(outgoing.Select(e => e.ToNodeId).ToList(), cancellationToken);
        return children.Count(n => n.NodeType == childNodeType);
    }

    public async Task<IReadOnlyList<EntityLink>> ListLinksAsync(
        Guid projectId,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        var node = await ResolveEntityNodeAsync(projectId, entityId, cancellationToken);
        if (node is null) return [];

        var adjacent = await edges.GetAdjacentAsync(
            node.Id,
            EdgeDirection.Both,
            edgeTypes: null,
            maxResults: null,
            cancellationToken);
        if (adjacent.Count == 0) return [];

        var otherIds = adjacent
            .Select(e => e.FromNodeId == node.Id ? e.ToNodeId : e.FromNodeId)
            .Distinct()
            .ToList();
        var others = (await nodes.GetByIdsAsync(otherIds, cancellationToken))
            .ToDictionary(n => n.Id);

        var result = new List<EntityLink>(adjacent.Count);
        foreach (var edge in adjacent)
        {
            var isOutgoing = edge.FromNodeId == node.Id;
            var otherId = isOutgoing ? edge.ToNodeId : edge.FromNodeId;
            if (!others.TryGetValue(otherId, out var other)) continue;
            var otherGuid = Guid.TryParseExact(other.Key, "N", out var g) ? g : Guid.Empty;
            result.Add(new EntityLink(
                EdgeId: edge.Id,
                EdgeType: edge.EdgeType,
                Direction: isOutgoing ? EntityLinkDirection.Outgoing : EntityLinkDirection.Incoming,
                OtherEntityId: otherGuid,
                OtherEntityName: other.Label ?? other.Key,
                OtherEntityType: other.NodeType,
                SortOrder: edge.SortOrder,
                Properties: ProjectProperties(edge.Properties)));
        }
        return result;
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// Resolves an id to a graph node. Accepts either an entity id (GUID we generated for
    /// the entity's <c>Key</c>) or a chapter id (also a GUID; matches <c>NodeType="Chapter"</c>).
    /// Auto-creates the chapter node if the id refers to a chapter that hasn't been
    /// touched by the graph layer yet (lazy upsert).
    /// </summary>
    private async Task<GraphNode?> ResolveEntityNodeAsync(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        var key = id.ToString("N");
        var existing = await nodes.FindByKeyAsync(projectId, key, cancellationToken);
        return existing;
    }

    /// <summary>
    /// Like <see cref="ResolveEntityNodeAsync"/> but lazy-upserts a chapter node when the id
    /// matches a chapter in the project. This is the only path that converts a chapter into
    /// a graph node, keeping empty chapters out of the graph.
    /// </summary>
    private async Task<GraphNode?> EnsureParentNodeAsync(Guid projectId, Guid parentId, CancellationToken cancellationToken)
    {
        var existing = await nodes.FindByKeyAsync(projectId, parentId.ToString("N"), cancellationToken);
        if (existing is not null) return existing;

        // Parent is unknown to the graph. The only legal lazy-upsertable parent today is a
        // Chapter. (Other parents must be created as entities first.) We can't validate that
        // the GUID actually maps to a chapter here without taking on a chapter dependency,
        // so we trust the caller and tag it as a Chapter node.
        return await graph.UpsertNodeAsync(
            projectId,
            ChapterNodeType,
            parentId.ToString("N"),
            label: null,
            properties: null,
            cancellationToken);
    }

    private async Task<Guid?> FindParentAsync(long childNodeId, CancellationToken cancellationToken)
    {
        var incoming = await edges.GetAdjacentAsync(
            childNodeId,
            EdgeDirection.Incoming,
            new[] { HasChildEdgeType },
            maxResults: 1,
            cancellationToken);
        if (incoming.Count == 0) return null;
        var parentNode = await nodes.GetByIdAsync(incoming[0].FromNodeId, cancellationToken);
        return parentNode is null ? null : Guid.ParseExact(parentNode.Key, "N");
    }

    private static StoryEntity Project(GraphNode node, Guid? parentId, int? orderOverride = null)
    {
        var props = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in node.Properties)
            props[kv.Key] = kv.Value?.ToString();

        // Entity id is the GUID we stored in Key. For lazily-created Chapter nodes the Key is
        // also a GUID (the ChapterId), so this round-trips cleanly.
        var id = Guid.TryParseExact(node.Key, "N", out var g) ? g : Guid.Empty;
        return new StoryEntity(
            Id: id,
            Type: node.NodeType,
            Name: node.Label ?? node.Key,
            Order: orderOverride,
            ParentId: parentId,
            Properties: props);
    }

    private static Dictionary<string, object?> ToObjectDict(IDictionary<string, string?>? src)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (src is null) return dict;
        foreach (var kv in src)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            dict[kv.Key] = kv.Value;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, string?> ProjectProperties(IDictionary<string, object?> source)
    {
        var props = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
            props[kv.Key] = kv.Value?.ToString();
        return props;
    }
}

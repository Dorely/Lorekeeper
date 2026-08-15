using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Knowledge;

/// <summary>
/// Relational implementation of <see cref="IGraphStore"/> backed by EF Core
/// <see cref="GraphNode"/>/<see cref="GraphEdge"/> tables. Multi-hop traversal is
/// performed in C# via iterative BFS — fine for the modest fan-out the narrative
/// graph is expected to hold; revisit with recursive CTEs (or a native graph DB)
/// when traversal cost becomes a real bottleneck.
/// <para>
/// Design constraints any future backend must honor:
/// <list type="bullet">
///   <item><c>UpsertNode</c> identity is <c>(ProjectId, NodeType, Key)</c>; properties merge-replace.</item>
///   <item><c>UpsertEdge</c> identity is <c>(FromNodeId, ToNodeId, EdgeType)</c>.</item>
///   <item>Removing a node cascades its edges.</item>
///   <item>Traversal honours direction + edge-type filters and stops at <c>Depth</c> hops.</item>
/// </list>
/// </para>
/// </summary>
public class RelationalGraphStore(
IAppDatabaseOperationFactory database) : IGraphStore
{
    public async Task<GraphNode> UpsertNodeAsync(
        Guid projectId,
        string nodeType,
        string key,
        string? label = null,
        IDictionary<string, object?>? properties = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var existing = await nodes.FindAsync(projectId, nodeType, key, cancellationToken);
        if (existing is null)
        {
            var node = new GraphNode
            {
                ProjectId = projectId,
                NodeType = nodeType,
                Key = key,
                Label = label,
                Properties = properties is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(properties),
            };
            await nodes.AddAsync(node, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            return node;
        }

        existing.Label = label ?? existing.Label;
        if (properties is not null)
            existing.Properties = new Dictionary<string, object?>(properties);
        existing.UpdatedAt = DateTime.UtcNow;
        nodes.Update(existing);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<GraphEdge> UpsertEdgeAsync(
        long fromNodeId,
        long toNodeId,
        string edgeType,
        IDictionary<string, object?>? properties = null,
        int? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        var existing = await edges.FindAsync(fromNodeId, toNodeId, edgeType, cancellationToken);
        if (existing is null)
        {
            var edge = new GraphEdge
            {
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                EdgeType = edgeType,
                Properties = properties is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(properties),
                SortOrder = sortOrder,
            };
            await edges.AddAsync(edge, cancellationToken);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            return edge;
        }

        if (properties is not null)
            existing.Properties = new Dictionary<string, object?>(properties);
        existing.SortOrder = sortOrder ?? existing.SortOrder;
        existing.UpdatedAt = DateTime.UtcNow;
        edges.Update(existing);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task RemoveNodeAsync(long nodeId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var node = await nodes.GetByIdAsync(nodeId, cancellationToken);
        if (node is null) return;
        nodes.Remove(node);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveEdgeAsync(long edgeId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var edges = databaseOperation.Repositories.GraphEdges;
        var edge = await edges.GetByIdAsync(edgeId, cancellationToken);
        if (edge is null) return;
        edges.Remove(edge);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<GraphNode?> GetNodeAsync(long nodeId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        return await nodes.GetByIdAsync(nodeId, cancellationToken);
    }
    public async Task<GraphNode?> FindNodeAsync(Guid projectId, string nodeType, string key, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var nodes = databaseOperation.Repositories.GraphNodes;
        return await nodes.FindAsync(projectId, nodeType, key, cancellationToken);
    }
    public async Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        long nodeId,
        GraphTraversalOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var edges = databaseOperation.Repositories.GraphEdges;
        var direction = ToEdgeDirection(options.Direction);
        var visitedNodeIds = new HashSet<long> { nodeId };
        var resultIds = new List<long>();
        var frontier = new List<long> { nodeId };

        for (var depth = 0; depth < options.Depth; depth++)
        {
            var nextFrontier = new List<long>();
            foreach (var current in frontier)
            {
                var adjacent = await edges.GetAdjacentAsync(
                    current, direction, options.EdgeTypes, options.MaxResults, cancellationToken);

                foreach (var edge in adjacent)
                {
                    var otherId = edge.FromNodeId == current ? edge.ToNodeId : edge.FromNodeId;
                    if (!visitedNodeIds.Add(otherId)) continue;
                    resultIds.Add(otherId);
                    nextFrontier.Add(otherId);
                    if (options.MaxResults is int max && resultIds.Count >= max)
                        return await nodes.GetByIdsAsync(resultIds, cancellationToken);
                }
            }
            frontier = nextFrontier;
            if (frontier.Count == 0) break;
        }

        var loaded = await nodes.GetByIdsAsync(resultIds, cancellationToken);
        var byId = loaded.ToDictionary(n => n.Id);
        return resultIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public async Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        long fromNodeId,
        long toNodeId,
        GraphTraversalOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var nodes = databaseOperation.Repositories.GraphNodes;
        var edges = databaseOperation.Repositories.GraphEdges;
        var start = await nodes.GetByIdAsync(fromNodeId, cancellationToken);
        if (start is null) return [];

        var direction = ToEdgeDirection(options.Direction);
        var paths = new List<GraphPath>();
        var queue = new Queue<List<GraphPathHop>>();
        queue.Enqueue([]);

        var hopByCurrent = new Dictionary<long, List<GraphPathHop>> { [fromNodeId] = [] };

        for (var depth = 0; depth < options.Depth; depth++)
        {
            var levelSize = queue.Count;
            if (levelSize == 0) break;

            for (var i = 0; i < levelSize; i++)
            {
                var hops = queue.Dequeue();
                var currentId = hops.Count == 0 ? fromNodeId : hops[^1].Node.Id;

                var adjacent = await edges.GetAdjacentAsync(
                    currentId, direction, options.EdgeTypes, null, cancellationToken);

                foreach (var edge in adjacent)
                {
                    var otherId = edge.FromNodeId == currentId ? edge.ToNodeId : edge.FromNodeId;
                    var otherNode = await nodes.GetByIdAsync(otherId, cancellationToken);
                    if (otherNode is null) continue;

                    var newHops = new List<GraphPathHop>(hops) { new(edge, otherNode) };

                    if (otherId == toNodeId)
                    {
                        paths.Add(new GraphPath(start, newHops));
                        if (options.MaxResults is int cap && paths.Count >= cap)
                            return paths;
                        continue;
                    }

                    queue.Enqueue(newHops);
                }
            }
        }

        return paths;
    }

    private static EdgeDirection ToEdgeDirection(GraphDirection direction) => direction switch
    {
        GraphDirection.Outgoing => EdgeDirection.Outgoing,
        GraphDirection.Incoming => EdgeDirection.Incoming,
        GraphDirection.Both => EdgeDirection.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
}

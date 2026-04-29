using Lorekeeper.Models;

namespace Lorekeeper.Knowledge;

public enum GraphDirection
{
    Outgoing,
    Incoming,
    Both
}

/// <summary>
/// Options controlling neighbor / path queries on <see cref="IGraphStore"/>.
/// </summary>
public sealed class GraphTraversalOptions
{
    public GraphDirection Direction { get; init; } = GraphDirection.Both;

    /// <summary>If null or empty, all edge types are followed.</summary>
    public IReadOnlyCollection<string>? EdgeTypes { get; init; }

    /// <summary>Hops to follow. <c>1</c> = direct neighbors only.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Hard cap on returned nodes / paths. Null means caller-bounded.</summary>
    public int? MaxResults { get; init; }
}

/// <summary>
/// A directed walk through the graph: alternating <c>(edge, node)</c> hops starting
/// from the source node (which is <see cref="Start"/>).
/// </summary>
public sealed record GraphPath(GraphNode Start, IReadOnlyList<GraphPathHop> Hops);

public sealed record GraphPathHop(GraphEdge Edge, GraphNode Node);

/// <summary>
/// Backend-agnostic typed-graph store. Today implemented over relational
/// <see cref="GraphNode"/>/<see cref="GraphEdge"/> tables; designed so a Cypher-backed
/// implementation (Neo4j, Apache AGE, Kuzu, ...) can drop in without changing callers.
/// </summary>
public interface IGraphStore
{
    Task<GraphNode> UpsertNodeAsync(
        string nodeType,
        string key,
        string? label = null,
        IDictionary<string, object?>? properties = null,
        CancellationToken cancellationToken = default);

    Task<GraphEdge> UpsertEdgeAsync(
        long fromNodeId,
        long toNodeId,
        string edgeType,
        IDictionary<string, object?>? properties = null,
        CancellationToken cancellationToken = default);

    Task RemoveNodeAsync(long nodeId, CancellationToken cancellationToken = default);
    Task RemoveEdgeAsync(long edgeId, CancellationToken cancellationToken = default);

    Task<GraphNode?> GetNodeAsync(long nodeId, CancellationToken cancellationToken = default);
    Task<GraphNode?> FindNodeAsync(string nodeType, string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(
        long nodeId,
        GraphTraversalOptions options,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GraphPath>> FindPathsAsync(
        long fromNodeId,
        long toNodeId,
        GraphTraversalOptions options,
        CancellationToken cancellationToken = default);
}

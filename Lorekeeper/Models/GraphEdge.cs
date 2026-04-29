namespace Lorekeeper.Models;

/// <summary>
/// A typed, directed edge between two <see cref="GraphNode"/>s. Multiple edges of the same
/// <see cref="EdgeType"/> are allowed between the same pair (e.g. multiple "Visited" events).
/// </summary>
public class GraphEdge
{
    public long Id { get; set; }
    public long FromNodeId { get; set; }
    public long ToNodeId { get; set; }

    /// <summary>Caller-defined edge category (KnowsAbout, Killed, LocatedIn, OccurredBefore, ...).</summary>
    public required string EdgeType { get; set; }

    /// <summary>Free-form properties serialised as JSON (TEXT column on SQLite).</summary>
    public Dictionary<string, object?> Properties { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public GraphNode FromNode { get; set; } = null!;
    public GraphNode ToNode { get; set; } = null!;
}

namespace Lorekeeper.Models;

/// <summary>
/// A node in the typed knowledge graph. Caller-provided <see cref="NodeType"/> + <see cref="Key"/>
/// form a stable, human-meaningful identity (e.g. <c>("Character", "argus-the-bold")</c>).
/// </summary>
public class GraphNode
{
    public long Id { get; set; }

    /// <summary>Caller-defined node category (Character, Location, Event, Lore, ...).</summary>
    public required string NodeType { get; set; }

    /// <summary>Stable identifier within <see cref="NodeType"/>. Unique together with <see cref="NodeType"/>.</summary>
    public required string Key { get; set; }

    public string? Label { get; set; }

    /// <summary>Free-form properties serialised as JSON (TEXT column on SQLite).</summary>
    public Dictionary<string, object?> Properties { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GraphEdge> OutgoingEdges { get; set; } = [];
    public ICollection<GraphEdge> IncomingEdges { get; set; } = [];
}

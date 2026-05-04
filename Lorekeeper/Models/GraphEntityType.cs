namespace Lorekeeper.Models;

/// <summary>
/// Lightweight project-scoped registry entry for a graph node type. It is intentionally
/// descriptive rather than restrictive: callers may still create arbitrary node types,
/// while the UI and LLM tools can use these rows for labels and defaults.
/// </summary>
public class GraphEntityType
{
    public long Id { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Canonical graph node type key, e.g. Character, Location, Act.</summary>
    public required string Type { get; set; }

    public required string SingularLabel { get; set; }
    public required string PluralLabel { get; set; }

    public string? Color { get; set; }
    public string? Icon { get; set; }

    /// <summary>True for structural outline nodes such as Project, Act, Chapter, and Event.</summary>
    public bool IsStructural { get; set; }

    /// <summary>True when this type normally lives under a chapter parent.</summary>
    public bool IsChapterScoped { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Optional default property keys/values suggested by the UI and LLM tools.</summary>
    public Dictionary<string, object?> DefaultProperties { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
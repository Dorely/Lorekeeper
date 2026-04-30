namespace Lorekeeper.Models;

/// <summary>
/// A single chapter belonging to a <see cref="Project"/>. Body is the source of truth
/// for vector indexing; <see cref="VectorIndexState"/> tracks whether the persisted
/// chunks in the vector store are in sync with the current <see cref="Body"/>.
/// </summary>
public class Chapter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Title { get; set; }

    public string Body { get; set; } = string.Empty;

    public string Synopsis { get; set; } = string.Empty;

    /// <summary>0-based display order within the project. Reordering rewrites this column.</summary>
    public int Order { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public VectorIndexState VectorIndexState { get; set; } = VectorIndexState.UpToDate;

    /// <summary>Timestamp of last successful reindex; null if never indexed.</summary>
    public DateTime? VectorIndexedAt { get; set; }

    /// <summary>Last reindex error message, when <see cref="VectorIndexState"/> is <see cref="VectorIndexState.Failed"/>.</summary>
    public string? VectorIndexError { get; set; }

    /// <summary>Stable vector-store source id for this chapter.</summary>
    public string VectorSourceId => Id.ToString("N");
}

public enum VectorIndexState
{
    UpToDate,
    Stale,
    Failed,
}

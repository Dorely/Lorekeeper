namespace Lorekeeper.Models;

/// <summary>
/// A top-level structural grouping of <see cref="Chapter"/>s within a <see cref="Project"/>
/// (e.g. "Act 1: Fractured Horde"). Acts are first-class entities so they can carry
/// ordering, synopsis, and AI-generated metadata.
/// </summary>
public class Act
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Title { get; set; }

    public string Synopsis { get; set; } = string.Empty;

    /// <summary>0-based display order within the project. Reordering rewrites this column.</summary>
    public int Order { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Chapter> Chapters { get; set; } = [];
}

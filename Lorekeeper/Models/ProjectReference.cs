namespace Lorekeeper.Models;

/// <summary>
/// A directed, read-only continuity link from one project to another.
/// </summary>
public sealed class ProjectReference
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReferencingProjectId { get; set; }
    public Project ReferencingProject { get; set; } = null!;

    /// <summary>
    /// Stable identity of the target version-history repository. This is
    /// intentionally not a local foreign key so the link survives on another
    /// machine where the target project is not present yet.
    /// </summary>
    public Guid ReferencedRepositoryId { get; set; }

    /// <summary>
    /// Stable identity of the target project inside the referenced repository.
    /// This is metadata rather than a foreign key; local resolution is tracked
    /// separately by <see cref="ResolvedProjectId"/>.
    /// </summary>
    public Guid ReferencedProjectId { get; set; }

    public Guid? ResolvedProjectId { get; set; }
    public Project? ResolvedProject { get; set; }

    public string ReferencedProjectName { get; set; } = string.Empty;
    public string ReferencedProjectSlug { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

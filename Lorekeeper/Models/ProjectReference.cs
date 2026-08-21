namespace Lorekeeper.Models;

/// <summary>
/// A directed, read-only continuity link from one project to another.
/// </summary>
public sealed class ProjectReference
{
    public Guid ReferencingProjectId { get; set; }
    public Project ReferencingProject { get; set; } = null!;

    public Guid ReferencedProjectId { get; set; }
    public Project ReferencedProject { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

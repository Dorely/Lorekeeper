namespace Lorekeeper.Models;

/// <summary>Author-owned project-wide world or research context, independent of book type.</summary>
public sealed class WorldBrief
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Content { get; set; } = string.Empty;
    public long Revision { get; set; }
}

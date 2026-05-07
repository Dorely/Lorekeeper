namespace Lorekeeper.Models;

/// <summary>
/// A project-scoped prose sample used as a future style reference for chapter generation.
/// </summary>
public class WritingSample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Title { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
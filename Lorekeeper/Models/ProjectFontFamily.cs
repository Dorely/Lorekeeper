namespace Lorekeeper.Models;

public class ProjectFontFamily
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Name { get; set; }
    public bool EmbeddingRightsConfirmed { get; set; }
    public string RightsDeclaration { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProjectFontFace> Faces { get; set; } = [];
}

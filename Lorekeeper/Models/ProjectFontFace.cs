namespace Lorekeeper.Models;

public class ProjectFontFace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FamilyId { get; set; }
    public ProjectFontFamily Family { get; set; } = null!;

    public required string SubfamilyName { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public int Weight { get; set; }
    public bool Italic { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}


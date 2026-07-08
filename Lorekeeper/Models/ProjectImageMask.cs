namespace Lorekeeper.Models;

public class ProjectImageMask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid ImageId { get; set; }
    public PublishAsset Image { get; set; } = null!;

    public string Label { get; set; } = string.Empty;

    public string ContentType { get; set; } = "image/png";

    public byte[] Data { get; set; } = [];

    public int Width { get; set; }

    public int Height { get; set; }

    public string OwnerKind { get; set; } = "image";

    public Guid OwnerId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

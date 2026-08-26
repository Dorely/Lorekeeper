namespace Lorekeeper.Models;

/// <summary>
/// A provider preview received while an image generation output was being built.
/// Partials are intentionally job-owned until an author explicitly promotes one
/// to a reusable project image.
/// </summary>
public class ProjectImagePartial
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid JobId { get; set; }
    public ProjectImageGenerationJob Job { get; set; } = null!;

    public int OutputIndex { get; set; }
    public int Attempt { get; set; }
    public int PartialImageIndex { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public byte[] Data { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string MainlineModel { get; set; } = string.Empty;
    public string ImageModel { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? ResponseId { get; set; }
    public string? CallId { get; set; }
    public string? ItemId { get; set; }
    public string? LastEventType { get; set; }
    public int EventCount { get; set; }

    public Guid? FinalOutputImageId { get; set; }
    public PublishAsset? FinalOutputImage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

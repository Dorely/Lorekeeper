namespace Lorekeeper.Models;

public class PublishAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public PublishAssetSource Source { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public byte[] Data { get; set; } = [];
    public string AltText { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string GenerationModel { get; set; } = string.Empty;
    public string SourceMetadataJson { get; set; } = string.Empty;

    public Guid? DerivedFromImageId { get; set; }
    public PublishAsset? DerivedFromImage { get; set; }
    public double? CropXPercent { get; set; }
    public double? CropYPercent { get; set; }
    public double? CropWidthPercent { get; set; }
    public double? CropHeightPercent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PublicationImagePlacement> PublicationPlacements { get; set; } = [];
    public ICollection<PublicationEdition> CoverEditions { get; set; } = [];
    public ICollection<ProjectImageMask> ImageMasks { get; set; } = [];

    public ICollection<ProjectImageChatAttachment> ImageChatAttachments { get; set; } = [];
    public ICollection<EntityVisualExample> EntityVisualExamples { get; set; } = [];
    public ICollection<SourceVisualCandidate> SourceVisualCandidates { get; set; } = [];
    public ICollection<PublishAsset> DerivedImages { get; set; } = [];
}

public enum PublishAssetSource
{
    // Explicit values are persisted; do not renumber.
    Uploaded = 0,
    Generated = 1,
    Edited = 2,
    Cropped = 3,
    Resized = 4,
    Imported = 5,
    Upscaled = 6,
}

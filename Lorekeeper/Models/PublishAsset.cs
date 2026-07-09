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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PublishProfile> CoverProfiles { get; set; } = [];
    public ICollection<PublishImagePlacement> ImagePlacements { get; set; } = [];
    public ICollection<ProjectImageMask> ImageMasks { get; set; } = [];

    public ICollection<ProjectImageChatAttachment> ImageChatAttachments { get; set; } = [];
    public ICollection<EntityVisualExample> EntityVisualExamples { get; set; } = [];
    public ICollection<SourceVisualCandidate> SourceVisualCandidates { get; set; } = [];
}

public enum PublishAssetSource
{
    Uploaded,
    Generated,
    Edited,
    Imported,
}

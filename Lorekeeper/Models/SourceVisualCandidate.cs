namespace Lorekeeper.Models;

public class SourceVisualCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid? IngestSourceId { get; set; }
    public IngestSource? IngestSource { get; set; }

    public Guid? WebIngestCandidateId { get; set; }
    public WebIngestCandidate? WebIngestCandidate { get; set; }

    public SourceVisualCandidateKind Kind { get; set; }
    public SourceVisualCandidateStatus Status { get; set; } = SourceVisualCandidateStatus.Discovered;

    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public byte[] Data { get; set; } = [];
    public string AltText { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Locator { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public string ContentHash { get; set; } = string.Empty;
    public int? StartChar { get; set; }
    public int? EndChar { get; set; }
    public Guid? PromotedImageId { get; set; }
    public PublishAsset? PromotedImage { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<EntityVisualExample> EntityVisualExamples { get; set; } = [];
}

public enum SourceVisualCandidateKind
{
    ResearchWeb,
    IngestArtifact,
}

public enum SourceVisualCandidateStatus
{
    Discovered,
    Inspected,
    Promoted,
    Skipped,
    Failed,
}

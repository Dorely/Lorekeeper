namespace Lorekeeper.Models;

public class IngestSourceBlock
{
    public const int MaximumNormalizedTextLength = 1_048_576;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;
    public Guid SourceExtractionVersionId { get; set; }
    public SourceExtractionVersion SourceExtractionVersion { get; set; } = null!;

    public Guid? SourcePageId { get; set; }
    public IngestSourcePage? SourcePage { get; set; }

    public int Index { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Locator { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public int StartChar { get; set; }
    public int EndChar { get; set; }
    public string NormalizedText { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SourceLocation> Locations { get; set; } = [];
}

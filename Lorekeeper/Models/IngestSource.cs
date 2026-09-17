namespace Lorekeeper.Models;

public class IngestSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string Title { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public required string UserInstructions { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public DateTime? FetchedAt { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string SourceMetadataJson { get; set; } = "{}";
    /// <summary>Current reading/indexing projection. Historical evidence always names its extraction explicitly.</summary>
    public Guid? ActiveExtractionVersionId { get; set; }

    public VectorIndexState VectorIndexState { get; set; } = VectorIndexState.Stale;
    public DateTime? VectorIndexedAt { get; set; }
    public string? VectorIndexError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public SourceOriginal? Original { get; set; }
    public ICollection<SourceExtractionVersion> ExtractionVersions { get; set; } = [];
    public ICollection<BibliographicRecord> BibliographicRecords { get; set; } = [];
    public ICollection<SourceLocation> Locations { get; set; } = [];
    public ICollection<IngestSourceChunk> SourceChunks { get; set; } = [];
    public ICollection<IngestSourcePage> SourcePages { get; set; } = [];
    public ICollection<IngestSourceBlock> SourceBlocks { get; set; } = [];
    public ICollection<IngestVectorFragment> VectorFragments { get; set; } = [];
    public ICollection<IngestJob> Jobs { get; set; } = [];
    public ICollection<IngestStagingRecord> StagingRecords { get; set; } = [];
    public ICollection<SourceVisualCandidate> VisualCandidates { get; set; } = [];
    public ICollection<BookBriefCanonSource> BookBriefCanonSelections { get; set; } = [];

    public string VectorSourceId => Id.ToString("N");
}

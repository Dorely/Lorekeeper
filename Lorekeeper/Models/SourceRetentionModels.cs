using System.ComponentModel.DataAnnotations.Schema;

namespace Lorekeeper.Models;

public enum SourceOriginalState
{
    Available,
    OriginalUnavailable,
}

public sealed class SourceOriginal
{
    public const int MaximumChunkBytes = 8 * 1024 * 1024;

    // This is deliberately the existing IngestSource identity. Source IDs already
    // occur in graph evidence, canonical selections, and historical payloads.
    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;

    public SourceOriginalState State { get; set; }
    public required string FileName { get; set; }
    public required string MediaType { get; set; }
    public long Length { get; set; }
    // OriginalUnavailable deliberately carries no reconstructed byte hash. The
    // extraction hash remains on SourceExtractionVersion instead.
    public string? Sha256 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SourceOriginalChunk> Chunks { get; set; } = [];

    [NotMapped]
    internal bool HasExternallyVerifiedBlobContent { get; set; }
}

/// <summary>
/// Content-addressed binary payload shared by one or more retained originals.
/// </summary>
public sealed class SourceOriginalBlob
{
    public required string Sha256 { get; set; }
    public int Length { get; set; }
    public byte[] Data { get; set; } = [];

    public ICollection<SourceOriginalChunk> Chunks { get; set; } = [];
}

public sealed class SourceOriginalChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public SourceOriginal SourceOriginal { get; set; } = null!;
    public int Index { get; set; }
    public required string BlobSha256 { get; set; }
    public SourceOriginalBlob Blob { get; set; } = null!;
    public int ByteLength { get; set; }
}

public enum SourceExtractionStatus
{
    Extracting,
    Ready,
    Failed,
    LegacyImmutable,
}

public sealed class SourceExtractionVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;
    public int Ordinal { get; set; }
    public required string Extractor { get; set; }
    public required string ExtractorVersion { get; set; }
    public string OptionsJson { get; set; } = "{}";
    public required string ContentHash { get; set; }
    public SourceExtractionStatus Status { get; set; }
    public string Diagnostics { get; set; } = string.Empty;
    public required string NormalizedText { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<IngestSourceChunk> SourceChunks { get; set; } = [];
    public ICollection<IngestSourcePage> SourcePages { get; set; } = [];
    public ICollection<IngestSourceBlock> SourceBlocks { get; set; } = [];
    public ICollection<SourceLocation> Locations { get; set; } = [];
}

public enum SourceLocationResolutionState
{
    Resolved,
    Unavailable,
    Outdated,
    Ambiguous,
}

/// <summary>
/// Durable evidence anchor. Consumers persist this record's immutable location
/// data rather than retargeting evidence to a newer extraction.
/// </summary>
public sealed class SourceLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid SourceId { get; set; }
    public IngestSource Source { get; set; } = null!;
    public Guid ExtractionVersionId { get; set; }
    public SourceExtractionVersion ExtractionVersion { get; set; } = null!;
    public Guid? SourceBlockId { get; set; }
    public IngestSourceBlock? SourceBlock { get; set; }
    public int? PageNumber { get; set; }
    public int NormalizedStart { get; set; }
    public int NormalizedLength { get; set; }
    public string Locator { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public required string VerificationHash { get; set; }
    public SourceLocationResolutionState ResolutionState { get; set; } = SourceLocationResolutionState.Resolved;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum BibliographicRecordKind
{
    Book,
    BookChapter,
    JournalArticle,
    MagazineArticle,
    NewspaperArticle,
    WebPage,
    Report,
    Thesis,
}

public sealed class BibliographicRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public Guid? SourceId { get; set; }
    public IngestSource? Source { get; set; }
    public BibliographicRecordKind Kind { get; set; }
    public required string Title { get; set; }
    public string ContainerTitle { get; set; } = string.Empty;
    public string AuthorsJson { get; set; } = "[]";
    public string EditorsJson { get; set; } = "[]";
    public string TranslatorsJson { get; set; } = "[]";
    public int? IssuedYear { get; set; }
    public int? IssuedMonth { get; set; }
    public int? IssuedDay { get; set; }
    public string Edition { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string PublisherPlace { get; set; } = string.Empty;
    public string Institution { get; set; } = string.Empty;
    public string ThesisType { get; set; } = string.Empty;
    public string Volume { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
    public string Pages { get; set; } = string.Empty;
    public string Doi { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? AccessedYear { get; set; }
    public int? AccessedMonth { get; set; }
    public int? AccessedDay { get; set; }
    public string Isbn { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record SourceDeletionUsageReport(
    Guid SourceId,
    int CanonicalSelectionCount,
    int BibliographicRecordCount,
    int JobCount,
    int OperationalJobCount,
    int SourceVisualCount,
    int GraphEvidenceCount,
    int SourceLocationCount)
{
    public bool HasLiveUsages => CanonicalSelectionCount > 0
        || BibliographicRecordCount > 0
        || JobCount > 0
        || OperationalJobCount > 0
        || SourceVisualCount > 0
        || GraphEvidenceCount > 0
        || SourceLocationCount > 0;
}

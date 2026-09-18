using Lorekeeper.Models;

namespace Lorekeeper.VersionHistory.Snapshots;

/// <summary>Small source catalogue stored at sources/index.json in schema v8.</summary>
public sealed record VersionHistorySourceIndex(
    IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<VersionHistoryBibliographicRecord> UnlinkedBibliographicRecords);

/// <summary>One schema-v8 source manifest stored in its stable source folder.</summary>
public sealed record VersionHistoryRetainedSource(
    Guid Id,
    string Title,
    string SourceKind,
    string Description,
    string Synopsis,
    string UserInstructions,
    string SourceUrl,
    string FinalUrl,
    string CanonicalUrl,
    string ContentType,
    string SourceMetadataJson,
    Guid? ActiveExtractionVersionId,
    VersionHistorySourceOriginal Original,
    IReadOnlyList<VersionHistorySourceExtraction> Extractions,
    IReadOnlyList<VersionHistoryBibliographicRecord> BibliographicRecords,
    IReadOnlyList<VersionHistorySourceLocation> Locations);

public sealed record VersionHistorySourceOriginal(
    SourceOriginalState State,
    string FileName,
    string MediaType,
    long Length,
    string? Sha256,
    IReadOnlyList<VersionHistorySourceOriginalChunk> Chunks);

public sealed record VersionHistorySourceOriginalChunk(
    Guid Id,
    int Index,
    string BlobSha256,
    int ByteLength);

public sealed record VersionHistorySourceExtraction(
    Guid Id,
    int Ordinal,
    string Extractor,
    string ExtractorVersion,
    string OptionsJson,
    string ContentHash,
    SourceExtractionStatus Status,
    string Diagnostics,
    string NormalizedText,
    IReadOnlyList<VersionHistorySourceChunk> Chunks,
    IReadOnlyList<VersionHistorySourcePage> Pages,
    IReadOnlyList<VersionHistorySourceBlock> Blocks);

public sealed record VersionHistorySourceChunk(
    Guid Id, int Index, string Title, string HeadingPath, int StartChar, int EndChar,
    int EstimatedTokenCount, string TokenCountMethod, string? TokenEncodingName,
    bool TokenCountIsExact, string Summary, string AgentNotes, IngestSourceChunkStructureStatus StructureStatus);

public sealed record VersionHistorySourcePage(
    Guid Id, int PageNumber, string Text, int StartChar, int EndChar, string ExtractionMethod,
    int Width, int Height, string ImageHash, string RenderSettingsJson, string VisionModelName, string Diagnostics);

public sealed record VersionHistorySourceBlock(
    Guid Id, Guid? SourcePageId, int Index, string Kind, string Title, string Locator,
    int? PageNumber, int StartChar, int EndChar, string NormalizedText, string ContentHash, string MetadataJson);

public sealed record VersionHistoryBibliographicRecord(
    Guid Id, Guid? SourceId, BibliographicRecordKind Kind, string Title, string ContainerTitle,
    string AuthorsJson, string EditorsJson, int? IssuedYear, string Publisher, string PublisherPlace,
    string Volume, string Issue, string Pages, string Doi, string Url, DateTime? AccessedAt,
    string Isbn, string Notes, string? TranslatorsJson, int? IssuedMonth, int? IssuedDay,
    string? Edition, string? Institution, string? ThesisType, int? AccessedYear,
    int? AccessedMonth, int? AccessedDay);

public sealed record VersionHistorySourceLocation(
    Guid Id, Guid SourceId, Guid ExtractionVersionId, Guid? SourceBlockId, int? PageNumber,
    int NormalizedStart, int NormalizedLength, string Locator, string Quote,
    string VerificationHash, SourceLocationResolutionState ResolutionState);

using Lorekeeper.Models;

namespace Lorekeeper.Sources;

public sealed record ProjectSourcesWorkspace(
    IReadOnlyList<ProjectSourceListItem> Sources,
    IReadOnlyList<ProjectSourceJobItem> Jobs,
    IReadOnlyList<ProjectBibliographicRecordItem> Bibliography);

public sealed record ProjectSourceListItem(
    Guid Id,
    string Title,
    string SourceKind,
    string MediaType,
    SourceOriginalState OriginalState,
    long OriginalLength,
    string? OriginalHash,
    Guid? ActiveExtractionVersionId,
    SourceExtractionStatus? ExtractionStatus,
    int BlockCount,
    int PageCount,
    DateTime UpdatedAt);

public sealed record ProjectSourceJobItem(
    Guid Id,
    Guid SourceId,
    string SourceTitle,
    IngestJobStatus Status,
    int TotalChunks,
    int CompletedChunks,
    string? Message,
    string? Error,
    DateTime UpdatedAt);

public sealed record ProjectBibliographicRecordItem(
    Guid Id,
    Guid? SourceId,
    BibliographicRecordKind Kind,
    string Title,
    string ContainerTitle,
    string AuthorsJson,
    int? IssuedYear,
    string Doi,
    string Url,
    DateTime UpdatedAt);

public sealed record ProjectSourceReading(
    Guid SourceId,
    string Title,
    string SourceKind,
    Guid ExtractionVersionId,
    SourceExtractionStatus ExtractionStatus,
    string Extractor,
    string ExtractorVersion,
    string Diagnostics,
    IReadOnlyList<ProjectSourceContentsItem> Contents,
    bool ContentsTruncated,
    ProjectSourceReadingBlock? SelectedBlock,
    IReadOnlyList<ProjectSourceEvidenceItem> Evidence);

public sealed record ProjectSourceContentsItem(
    Guid BlockId,
    int Index,
    string Title,
    string Kind,
    string Locator,
    int? PageNumber,
    int StartChar,
    int EndChar);

public sealed record ProjectSourceReadingBlock(
    Guid Id,
    string Title,
    string Kind,
    string Locator,
    int? PageNumber,
    int StartChar,
    int EndChar,
    string Text,
    bool IsTruncated,
    IReadOnlyList<ProjectSourceReadingSegment> Segments);

public sealed record ProjectSourceReadingSegment(
    string Text,
    SourceLocationResolutionState? EvidenceState,
    string? EvidenceLocator);

public sealed record ProjectSourceEvidenceItem(
    Guid Id,
    SourceLocationResolutionState State,
    Guid ExtractionVersionId,
    Guid? SourceBlockId,
    string Locator,
    string Quote,
    int? PageNumber,
    int NormalizedStart,
    int NormalizedLength);

public sealed record ProjectSourceSearchHit(
    Guid SourceId,
    Guid BlockId,
    string SourceTitle,
    string BlockTitle,
    string Locator,
    int? PageNumber,
    IReadOnlyList<ProjectSourceTextSegment> Excerpt);

public sealed record ProjectSourceTextSegment(string Text, bool IsMatch);

public sealed record ProjectSourceOriginalDownload(
    Guid ProjectId,
    Guid SourceId,
    string FileName,
    string MediaType,
    long Length,
    string Sha256,
    IReadOnlyList<ProjectSourceOriginalChunk> Chunks);

public sealed record ProjectSourceOriginalChunk(int Index, string Sha256, int Length);

public sealed record ProjectSourcePdfPage(byte[] Data, int Width, int Height);

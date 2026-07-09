namespace Lorekeeper.Ingest;

public interface IBookArtifactPreprocessor
{
    Task<BookArtifactPreprocessResult> PreprocessAsync(
        BookArtifactPreprocessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record BookArtifactPreprocessRequest(
    string FileName,
    string? ContentType,
    byte[] Bytes,
    int? ProviderId,
    IngestExtractionProfile ExtractionProfile,
    PdfArtifactIngestOptions PdfOptions);

public sealed record BookArtifactPreprocessResult(
    string SourceText,
    string SourceKind,
    string ContentType,
    string SourceMetadataJson,
    IReadOnlyList<IngestSourcePageDraft> Pages,
    IReadOnlyList<IngestSourceBlockDraft> Blocks,
    IReadOnlyList<IngestVisualCandidateDraft> Visuals,
    bool UsedVision,
    string Diagnostics);

public sealed record IngestSourcePageDraft(
    Guid Id,
    int PageNumber,
    string Text,
    int StartChar,
    int EndChar,
    string ExtractionMethod,
    int Width,
    int Height,
    string ImageHash,
    string RenderSettingsJson,
    int? VisionProviderId,
    string VisionModelName,
    string Diagnostics);

public sealed record IngestSourceBlockDraft(
    Guid Id,
    Guid? SourcePageId,
    int Index,
    string Kind,
    string Title,
    string Locator,
    int? PageNumber,
    int StartChar,
    int EndChar,
    string MetadataJson);

public sealed record IngestVisualCandidateDraft(
    string FileName,
    string ContentType,
    byte[] Data,
    string AltText,
    string Caption,
    string Locator,
    int? PageNumber,
    int? StartChar,
    int? EndChar,
    string MetadataJson);

namespace Lorekeeper.Ingest;

public sealed record IngestCreateJobRequest(
    string Title,
    string SourceText,
    string UserInstructions,
    string? SourceKind = null,
    string? Description = null,
    int? ProviderId = null,
    string? EncodingName = null,
    int? SourceTextTargetTokens = null,
    string? SourceUrl = null,
    string? FinalUrl = null,
    string? CanonicalUrl = null,
    DateTime? FetchedAt = null,
    string? ContentType = null,
    string? SourceMetadataJson = null);
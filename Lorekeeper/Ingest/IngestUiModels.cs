using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

public sealed record IngestJobListItem(
    Guid Id,
    Guid ProjectId,
    Guid SourceId,
    string SourceTitle,
    string SourceKind,
    IngestJobStatus Status,
    int TotalSourceChunks,
    int CompletedSourceChunks,
    int CreatedEntityCount,
    int CreatedRelationshipCount,
    string? CurrentMessage,
    string? ErrorMessage,
    int? ProviderId,
    string? ModelName,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record IngestJobDetailView(
    Guid Id,
    Guid ProjectId,
    Guid SourceId,
    string SourceTitle,
    string SourceKind,
    string SourceDescription,
    string Instructions,
    IngestJobStatus Status,
    int TotalSourceChunks,
    int CompletedSourceChunks,
    int CreatedEntityCount,
    int CreatedRelationshipCount,
    string? CurrentMessage,
    string? ErrorMessage,
    int? ProviderId,
    string? ModelName,
    string? EncodingName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<IngestJobChunkProgress> Chunks,
    IReadOnlyList<IngestJobEventView> Events);

public sealed record IngestJobChunkProgress(
    Guid Id,
    Guid SourceChunkId,
    int SourceChunkIndex,
    IngestJobChunkStatus Status,
    string Summary,
    string? ErrorMessage,
    int CreatedEntityCount,
    int CreatedRelationshipCount,
    string SourceChunkTitle,
    string HeadingPath,
    int StartChar,
    int EndChar,
    int EstimatedTokenCount,
    string TokenCountMethod,
    string? TokenEncodingName,
    bool TokenCountIsExact,
    string SourceChunkSummary,
    string AgentNotes);

public sealed record IngestReportItemView(
    Guid Id,
    Guid? SourceChunkId,
    IngestReportItemKind Kind,
    IngestReportItemStatus Status,
    string Title,
    string Summary,
    string Notes,
    string Evidence,
    string ResourceType,
    Guid? EntityId,
    long? GraphNodeId,
    long? GraphEdgeId,
    string PayloadJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record IngestJobEventView(
    Guid Id,
    IngestJobEventLevel Level,
    string EventType,
    string Message,
    string PayloadJson,
    DateTime CreatedAt);

public sealed record IngestSourceChunkExcerpt(
    Guid SourceChunkId,
    string Text,
    bool IsTruncated);
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
    DateTime UpdatedAt,
    IngestJobMode Mode = IngestJobMode.ExtractEntities);

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
    IngestFinalizationProgressView Finalization,
    IReadOnlyList<IngestJobEventView> Events,
    IngestJobMode Mode = IngestJobMode.ExtractEntities);

public enum IngestFinalizationPhase
{
    Pending,
    ReviewingEntities,
    BuildingRelationships,
    Completed,
    Failed,
}

public sealed record IngestFinalizationProgressView(
    IngestFinalizationPhase Phase,
    int EntityTotal,
    int EntityFinalized,
    int EntityActive,
    int EntityFailed,
    int RelationshipTotal,
    int RelationshipActive,
    int RelationshipFinalized,
    int RelationshipFailed,
    int SourceChunkNoteTotal,
    int SourceChunkNoteActive,
    int SourceChunkNoteFinalized,
    int SourceChunkNoteFailed,
    string CurrentMessage);

public sealed record IngestJobChunkProgress(
    Guid Id,
    Guid SourceChunkId,
    int SourceChunkIndex,
    IngestJobChunkStatus Status,
    string Summary,
    string? ErrorMessage,
    int CreatedEntityCount,
    int CreatedRelationshipCount,
    int? LlmTokenCount,
    bool? LlmTokenCountIsExact,
    string? LlmTokenCountMethod,
    string? LlmTokenEncodingName,
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
    int? SourceChunkIndex,
    string? SourceChunkTitle,
    IngestStagingRecordKind Kind,
    IngestStagingRecordStatus Status,
    string Title,
    string Summary,
    string Notes,
    string ResourceType,
    Guid? EntityId,
    Guid? FromEntityId,
    Guid? ToEntityId,
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

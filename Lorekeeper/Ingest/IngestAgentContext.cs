namespace Lorekeeper.Ingest;

public sealed record IngestAgentContext(
    Guid ProjectId,
    Guid JobId,
    Guid SourceId,
    string SourceTitle,
    string SourceKind,
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    Action OnMutated);
namespace Lorekeeper.Ingest;

public sealed record IngestAgentContext(
    Guid ProjectId,
    Guid JobId,
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    Action OnMutated);
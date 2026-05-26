namespace Lorekeeper.Ingest;

public interface IIngestJobNotifier
{
    IIngestJobUpdateSubscription Subscribe(Guid projectId);
    void Notify(IngestJobUpdate update);
}

public interface IIngestJobUpdateSubscription : IAsyncDisposable
{
    IAsyncEnumerable<IngestJobUpdate> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed record IngestJobUpdate(
    Guid ProjectId,
    Guid JobId,
    IngestJobUpdateKind Kind,
    DateTime CreatedAtUtc,
    IngestLiveUpdate? Live = null);

public enum IngestJobUpdateKind
{
    Created,
    Queued,
    Progress,
    Report,
    Live,
    Event,
    Completed,
    Stopped,
    Failed,
    Deleted,
}

public abstract record IngestLiveUpdate(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle);

public sealed record IngestLiveTurnStarted(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    int Attempt,
    int MaxAttempts) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveTextDelta(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    string Text) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveToolCallStarted(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    bool ArgumentsComplete) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveToolCallArgumentsDelta(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    string CallId,
    string ArgumentsDelta,
    bool ArgumentsComplete) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveToolCallCompleted(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    string CallId,
    string ToolName,
    string? Result,
    string? Error,
    double DurationMs) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveRetryScheduled(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    int Attempt,
    int MaxAttempts,
    int DelayMs,
    string Error) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveTurnCompleted(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

public sealed record IngestLiveTurnFailed(
    Guid SourceChunkId,
    int SourceChunkIndex,
    string SourceChunkTitle,
    string Error) : IngestLiveUpdate(SourceChunkId, SourceChunkIndex, SourceChunkTitle);

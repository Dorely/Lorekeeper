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
    DateTime CreatedAtUtc);

public enum IngestJobUpdateKind
{
    Created,
    Queued,
    Progress,
    Report,
    Event,
    Completed,
    Stopped,
    Failed,
    Deleted,
}
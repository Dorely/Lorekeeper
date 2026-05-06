namespace Lorekeeper.Ingest;

public interface IIngestJobQueue
{
    void Enqueue(Guid jobId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken);
    void RegisterCancellation(Guid jobId, CancellationTokenSource cancellationTokenSource);
    bool RequestCancellation(Guid jobId);
    void ClearCancellation(Guid jobId);
}
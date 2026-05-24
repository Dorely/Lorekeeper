using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

public interface IIngestVectorIndexingService
{
    Task EnsureVectorFragmentsAsync(IngestSource source, bool force = false, CancellationToken cancellationToken = default);
}


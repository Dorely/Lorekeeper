using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

public interface IIngestGraphSync
{
    Task EnsureSourceAsync(IngestSource source, IReadOnlyList<IngestSourceChunk> sourceChunks, CancellationToken cancellationToken = default);
    Task RemoveSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
}
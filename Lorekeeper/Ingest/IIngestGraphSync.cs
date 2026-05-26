using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

public interface IIngestGraphSync
{
    Task EnsureSourceAsync(
        IngestSource source,
        IReadOnlyList<IngestSourceChunk> sourceChunks,
        IReadOnlyList<IngestSourceBlock>? sourceBlocks = null,
        CancellationToken cancellationToken = default);

    Task RemoveSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
}

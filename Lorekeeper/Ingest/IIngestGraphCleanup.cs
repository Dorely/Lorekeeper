using Lorekeeper.Models;

namespace Lorekeeper.Ingest;

public interface IIngestGraphCleanup
{
    Task<IngestGraphCleanupResult> RemoveSourceGraphContributionsAsync(
        Guid projectId,
        Guid sourceId,
        IEnumerable<IngestStagingRecord> stagingRecords,
        CancellationToken cancellationToken = default);
}

public sealed record IngestGraphCleanupResult(
    int NodesUpdated,
    int NodesDeleted,
    int EdgesUpdated,
    int EdgesDeleted,
    int ExtractedFromEdgesDeleted,
    IReadOnlyCollection<Guid> EntityIdsToReindex,
    IReadOnlyCollection<Guid> EntityIdsToDelete);

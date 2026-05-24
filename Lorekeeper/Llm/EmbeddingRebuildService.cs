using Lorekeeper.Context;
using Lorekeeper.Chapters;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Llm;

public sealed class EmbeddingRebuildService(
    IEmbeddingConfigurationService configurations,
    IVectorStoreMaintenance vectorMaintenance,
    IProjectRepository projects,
    IChapterRepository chapters,
    IActRepository acts,
    IGraphNodeRepository nodes,
    IIngestRepository ingest,
    IChapterService chapterService,
    IContextIndexingService contextIndexing,
    IIngestVectorIndexingService ingestVectorIndexing,
    IOptions<EmbeddingRebuildOptions> options,
    ILogger<EmbeddingRebuildService> logger)
{
    private int _itemsSinceDelay;

    public async Task RebuildAsync(EmbeddingRebuildRequest request, CancellationToken cancellationToken = default)
    {
        var active = await configurations.GetActiveAsync(cancellationToken);
        if (active is null || active.Dimensions <= 0)
        {
            logger.LogInformation("Embedding rebuild skipped because no active embedding configuration exists.");
            return;
        }

        logger.LogInformation(
            "Starting embedding rebuild for configuration {ConfigurationId} ({Dimensions} dimensions).",
            request.ConfigurationId,
            active.Dimensions);

        await vectorMaintenance.RecreateAsync(active.Dimensions, cancellationToken);

        foreach (var project in await projects.ListAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await MarkProjectIndexesStaleAsync(project.Id, cancellationToken);

            foreach (var chapter in await chapters.ListByProjectAsync(project.Id, cancellationToken))
                await RunThrottledAsync(() => chapterService.ReindexAsync(chapter.Id, cancellationToken), cancellationToken);

            foreach (var source in await ingest.ListSourcesByProjectAsync(project.Id, cancellationToken))
                await RunThrottledAsync(() => ingestVectorIndexing.EnsureVectorFragmentsAsync(source, force: true, cancellationToken), cancellationToken);

            foreach (var node in await nodes.ListByProjectAsync(project.Id, cancellationToken))
            {
                if (!Guid.TryParseExact(node.Key, "N", out var entityId) || !IsContextEntityType(node.NodeType))
                    continue;

                await RunThrottledAsync(() => contextIndexing.ReindexEntityAsync(project.Id, entityId, cancellationToken), cancellationToken);
            }

            foreach (var act in await acts.ListByProjectAsync(project.Id, cancellationToken))
                await RunThrottledAsync(() => contextIndexing.ReindexActAsync(act.Id, cancellationToken), cancellationToken);

            foreach (var source in await ingest.ListSourcesByProjectAsync(project.Id, cancellationToken))
            {
                await RunThrottledAsync(() => contextIndexing.ReindexIngestSourceAsync(source.Id, cancellationToken), cancellationToken);
                foreach (var sourceChunk in await ingest.ListSourceChunksAsync(source.Id, cancellationToken))
                    await RunThrottledAsync(() => contextIndexing.ReindexIngestSourceChunkAsync(sourceChunk.Id, cancellationToken), cancellationToken);
            }
        }

        logger.LogInformation("Embedding rebuild completed.");
    }

    private async Task MarkProjectIndexesStaleAsync(Guid projectId, CancellationToken cancellationToken)
    {
        foreach (var chapter in await chapters.ListByProjectAsync(projectId, cancellationToken))
        {
            chapter.VectorIndexState = string.IsNullOrWhiteSpace(chapter.Body)
                ? VectorIndexState.UpToDate
                : VectorIndexState.Stale;
            chapter.VectorIndexedAt = null;
            chapter.VectorIndexError = null;
            chapters.Update(chapter);
        }

        foreach (var source in await ingest.ListSourcesByProjectAsync(projectId, cancellationToken))
        {
            source.VectorIndexState = VectorIndexState.Stale;
            source.VectorIndexedAt = null;
            source.VectorIndexError = null;
            source.UpdatedAt = DateTime.UtcNow;
            ingest.UpdateSource(source);
        }

        await chapters.SaveChangesAsync(cancellationToken);
        await ingest.SaveChangesAsync(cancellationToken);
    }

    private async Task RunThrottledAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var maxRetries = Math.Max(0, opts.MaxRetries);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await work();
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delayMs = Math.Max(100, opts.RetryDelayMilliseconds) * (attempt + 1);
                logger.LogWarning(ex, "Embedding rebuild item failed; retrying in {DelayMs} ms.", delayMs);
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        var batchSize = Math.Max(1, opts.BatchSize);
        _itemsSinceDelay++;
        var delay = Math.Max(0, opts.DelayBetweenBatchesMilliseconds);
        if (_itemsSinceDelay >= batchSize && delay > 0)
        {
            _itemsSinceDelay = 0;
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsContextEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase);
}

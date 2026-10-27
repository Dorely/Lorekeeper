using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Startup;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.ExternalAccess;

/// <summary>Retries only captured projection targets; never replays an authored mutation.</summary>
internal sealed class ExternalProjectionRepairWorker(IServiceScopeFactory scopes, IApplicationStartupState startup,
    ILogger<ExternalProjectionRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await startup.WaitForDatabaseReadyAsync(stoppingToken)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                if (startup.Current.Status != ApplicationStartupStatus.Ready) continue;
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<IAppDatabaseOperationFactory>();
                Guid[] ids;
                await using (var read = await database.OpenReadAsync(stoppingToken))
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-10);
                    var retry = DateTime.UtcNow.AddMinutes(-1);
                    ids = await read.Db.ExternalMutationReceipts.Where(value => value.RepairPending && value.CreatedAt < cutoff
                        && (value.RepairAttemptedAt == null || value.RepairAttemptedAt < retry)).OrderBy(value => value.CreatedAt)
                        .Select(value => value.Id).Take(5).ToArrayAsync(stoppingToken);
                }
                foreach (var id in ids)
                {
                    try
                    {
                        await using var repairScope = scopes.CreateAsyncScope();
                        await RepairAsync(repairScope.ServiceProvider, id, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { logger.LogDebug("Retained external repair {ReceiptId} will retry.", id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogDebug("External projection repair will retry after a temporary failure."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static async Task RepairAsync(IServiceProvider services, Guid id, CancellationToken ct)
    {
        var database = services.GetRequiredService<IAppDatabaseOperationFactory>();
        ExternalMutationReceipt receipt;
        await using (var write = await database.OpenWriteAsync(ct))
        {
            receipt = await write.Db.ExternalMutationReceipts.SingleAsync(value => value.Id == id, ct);
            if (!receipt.RepairPending) return;
            receipt.RepairAttemptedAt = DateTime.UtcNow;
            await write.SaveChangesAsync(ct);
        }
        var coordinator = services.GetRequiredService<IVectorIndexWorkCoordinator>();
        await using var deferral = coordinator.BeginDeferral();
        var targets = JsonSerializer.Deserialize<VectorIndexRepairTarget[]>(receipt.RepairTargetsJson, ExternalToolCatalog.Json) ?? [];
        var indexing = services.GetRequiredService<IContextIndexingService>();
        if (receipt.ProjectId is Guid projectId)
        {
            foreach (var target in targets)
            {
                var parts = target.ResourceKey.Split(':');
                var resource = Guid.Parse(parts[^1]);
                switch (target.Kind)
                {
                    case VectorIndexWorkKind.ContextEntity:
                        if (Guid.Parse(parts[0]) != projectId) throw new InvalidDataException("Repair target project mismatch.");
                        await indexing.ReindexEntityAsync(projectId, resource, ct);
                        break;
                    case VectorIndexWorkKind.ContextAct:
                        await indexing.ReindexActAsync(resource, ct);
                        break;
                    case VectorIndexWorkKind.ContextProjectProfile:
                        if (resource != projectId) throw new InvalidDataException("Repair target project mismatch.");
                        await indexing.ReindexProjectProfileAsync(projectId, ct);
                        break;
                    case VectorIndexWorkKind.ContextWritingSample:
                        await indexing.ReindexWritingSampleAsync(resource, ct);
                        break;
                    case VectorIndexWorkKind.ContextChapter:
                        await indexing.ReindexChapterAsync(resource, ct);
                        break;
                    case VectorIndexWorkKind.ChapterBody:
                        await services.GetRequiredService<IChapterService>().ReindexAsync(resource, ct);
                        break;
                    case VectorIndexWorkKind.EditionChapter:
                        await services.GetRequiredService<IManuscriptService>().RefreshDerivedStateAsync(EditorContentTarget.ForEdition(Guid.Parse(parts[0])), resource, ct);
                        break;
                    default: throw new InvalidDataException("Unknown retained projection target.");
                }
            }
            await services.GetRequiredService<IProjectImageGenerationRuntime>().EnqueueProjectAsync(projectId, ct);
            await using var read = await database.OpenReadAsync(ct);
            var publications = await read.Db.PublicationPreparationJobs.Where(value => value.ProjectId == projectId && value.Status == PublicationPreparationStatus.Queued)
                .Select(value => value.Id).ToArrayAsync(ct);
            foreach (var job in publications) await services.GetRequiredService<IPublicationPreparationQueue>().EnqueueAsync(job, ct);
            var ingest = await read.Db.IngestJobs.Where(value => value.ProjectId == projectId && value.Status == IngestJobStatus.Queued)
                .Select(value => value.Id).ToArrayAsync(ct);
            foreach (var job in ingest) services.GetRequiredService<IIngestJobQueue>().Enqueue(job);
        }
        await deferral.FlushAsync(ct);
        if (deferral.HasFailures) return;
        await using var complete = await database.OpenWriteAsync(ct);
        var current = await complete.Db.ExternalMutationReceipts.SingleAsync(value => value.Id == id, ct);
        current.RepairPending = false;
        var result = JsonNode.Parse(current.ResultJson)!.AsObject();
        result["repairStatus"] = "complete";
        current.ResultJson = result.ToJsonString(ExternalToolCatalog.Json);
        await complete.SaveChangesAsync(ct);
    }
}

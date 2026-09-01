using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublicationImageUpscalingMigrationTests
{
    private const string PreviousMigration = "20260830235957_CachePreparedInteriorPaginationV34";

    [Fact]
    public async Task MigrationPreservesJobsAndReclassifiesOnlyHistoricalPrintUpscales()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "publication-image-upscaling.db")}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var importedId = Guid.NewGuid();
            var historicalUpscaleId = Guid.NewGuid();
            var ordinaryResizeId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Projects
                        (Id, ReviewEditsEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ({projectId}, 1, 0, {now}, 1, 'Publication image migration fixture', '',
                         {$"publication-image-migration-{projectId:N}"}, {now});
                    """);

                db.PublishAssets.AddRange(
                    Asset(importedId, projectId, PublishAssetSource.Imported, "imported.png", "{}", now),
                    Asset(
                        historicalUpscaleId,
                        projectId,
                        PublishAssetSource.Resized,
                        "historical-upscale.png",
                        "{\"Transform\":{\"Kind\":\"print-resample\"}}",
                        now),
                    Asset(
                        ordinaryResizeId,
                        projectId,
                        PublishAssetSource.Resized,
                        "ordinary-resize.png",
                        "{\"Transform\":{\"Kind\":\"resize\"}}",
                        now));
                await db.SaveChangesAsync();

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO PublicationPreparationJobs
                        (Id, ProjectId, TargetKind, EditionId, RenderJobId, Status, Step,
                         ProgressPercent, Message, DiagnosticsJson, SourceFingerprint,
                         CancellationRequested, CreatedAt, StartedAt, CompletedAt)
                    VALUES
                        ({jobId}, {projectId}, 'CoreBook', NULL, NULL, 'Ready', 'Complete',
                         100, 'Preserved', '[]', 'fixture-fingerprint',
                         0, {now}, {now}, {now});
                    """);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var assets = await db.PublishAssets.AsNoTracking()
                    .Where(item => item.ProjectId == projectId)
                    .ToDictionaryAsync(item => item.Id);
                Assert.Equal(PublishAssetSource.Imported, assets[importedId].Source);
                Assert.Equal(PublishAssetSource.Upscaled, assets[historicalUpscaleId].Source);
                Assert.Equal(PublishAssetSource.Resized, assets[ordinaryResizeId].Source);

                var job = await db.PublicationPreparationJobs.AsNoTracking()
                    .SingleAsync(item => item.Id == jobId);
                Assert.Equal("Preserved", job.Message);
                Assert.Equal("{}", job.ImagePreparationSummaryJson);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static PublishAsset Asset(
        Guid id,
        Guid projectId,
        PublishAssetSource source,
        string fileName,
        string metadata,
        DateTime now) => new()
        {
            Id = id,
            ProjectId = projectId,
            Source = source,
            FileName = fileName,
            ContentType = "image/png",
            Data = [1, 2, 3],
            SourceMetadataJson = metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };
}

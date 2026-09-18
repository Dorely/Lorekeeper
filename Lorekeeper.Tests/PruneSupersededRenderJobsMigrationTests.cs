using System.Security.Cryptography;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PruneSupersededRenderJobsMigrationTests
{
    private const string PreviousMigration = "20260902171221_ScopedPublicationRenderingV36";

    [Fact]
    public async Task PruneMigrationKeepsNewestArtifactBearingTerminalJobPerEditionAndScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "prune-render-jobs.db")}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var firstEditionId = Guid.NewGuid();
            var secondEditionId = Guid.NewGuid();
            var firstEditionOldJobId = Guid.NewGuid();
            var firstEditionNewestJobId = Guid.NewGuid();
            var firstEditionFailedJobId = Guid.NewGuid();
            var firstEditionQueuedJobId = Guid.NewGuid();
            var firstEditionRenderingJobId = Guid.NewGuid();
            var secondEditionJobId = Guid.NewGuid();
            var bytes = "%PDF-1.7\nprune-history"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var now = DateTime.UtcNow;

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await DatabaseStartupMigrationService.EnsureCitationCompatibilityColumnsAsync(db, default);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Projects
                        (Id, ReviewEditsEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ({projectId}, 1, 0, {now}, 1, 'Prune publishing', '',
                         {$"prune-{projectId:N}"}, {now});
                    """);
                db.PublicationEditions.AddRange(
                    new PublicationEdition
                    {
                        Id = firstEditionId,
                        ProjectId = projectId,
                        Name = "Paperback",
                        Format = PublicationEditionFormat.Paperback,
                        CreatedAt = now,
                        UpdatedAt = now,
                    },
                    new PublicationEdition
                    {
                        Id = secondEditionId,
                        ProjectId = projectId,
                        Name = "Hardcover",
                        Format = PublicationEditionFormat.Hardcover,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                await db.SaveChangesAsync();
                await InsertRenderAsync(db, firstEditionOldJobId, firstEditionId, "Completed", 0,
                    now.AddMinutes(-30), bytes, hash, $"{firstEditionOldJobId}-old.pdf");
                await InsertRenderAsync(db, firstEditionNewestJobId, firstEditionId, "Completed", 0,
                    now.AddMinutes(-10), bytes, hash, $"{firstEditionNewestJobId}-newest.pdf");
                await InsertRenderAsync(db, firstEditionFailedJobId, firstEditionId, "Failed", 0,
                    now.AddMinutes(-20), bytes, hash, $"{firstEditionFailedJobId}-failed.pdf");
                await InsertRenderAsync(db, firstEditionQueuedJobId, firstEditionId, "Queued", 0,
                    now.AddMinutes(-5), null, null, null);
                await InsertRenderAsync(db, firstEditionRenderingJobId, firstEditionId, "Rendering", 1,
                    now.AddMinutes(-2), null, null, null);
                await InsertRenderAsync(db, secondEditionJobId, secondEditionId, "Completed", 0,
                    now.AddMinutes(-15), bytes, hash, $"{secondEditionJobId}-second.pdf");
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO PublicationPageMapEntries
                        (Id, RenderJobId, ChapterId, BlockId, PageNumber)
                    VALUES
                        ({Guid.NewGuid()}, {firstEditionOldJobId}, {Guid.NewGuid()}, {Guid.NewGuid()}, 1),
                        ({Guid.NewGuid()}, {firstEditionNewestJobId}, {Guid.NewGuid()}, {Guid.NewGuid()}, 1);
                    """);
                await DatabaseStartupMigrationService.RemoveCitationCompatibilityColumnsAsync(db, default);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var jobIds = await db.PublicationRenderJobs.AsNoTracking()
                    .Select(item => item.Id)
                    .ToListAsync();
                foreach (var expectedId in new[]
                         {
                             firstEditionNewestJobId,
                             firstEditionQueuedJobId,
                             firstEditionRenderingJobId,
                             secondEditionJobId,
                         })
                    Assert.Contains(expectedId, jobIds);
                Assert.Equal(4, jobIds.Count);
                Assert.True(await db.PublicationArtifacts.AsNoTracking()
                    .AnyAsync(artifact => artifact.RenderJobId == firstEditionNewestJobId));
                Assert.True(await db.PublicationArtifacts.AsNoTracking()
                    .AnyAsync(artifact => artifact.RenderJobId == secondEditionJobId));
                Assert.Equal(0, await db.PublicationPageMapEntries.AsNoTracking()
                    .Where(entry => entry.RenderJobId == firstEditionOldJobId)
                    .CountAsync());
                Assert.Equal(1, await db.PublicationPageMapEntries.AsNoTracking()
                    .Where(entry => entry.RenderJobId == firstEditionNewestJobId)
                    .CountAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task InsertRenderAsync(
        AppDbContext db,
        Guid jobId,
        Guid editionId,
        string status,
        int? interiorPageCount,
        DateTime createdAt,
        byte[]? data,
        string? hash,
        string? fileName)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PublicationRenderJobs
                (Id, CancellationRequested, CompletedAt, CreatedAt, DiagnosticsJson, EditionId,
                 EvidenceJson, InteriorPageCount, IsLegacy, PaginationFingerprint, ProfileId,
                 ProgressMessage, ProgressPercent, ProjectId, RendererVersion, Scope,
                 SourceFingerprint, StartedAt, Status, TargetKind)
            VALUES
                ({jobId}, 0, {((status is "Completed" or "Failed") ? (DateTime?)createdAt : null)}, {createdAt}, '[]',
                 {editionId}, json_object(), {interiorPageCount}, 0, 'pagination', 'fixture',
                 {status}, 100,
                 (SELECT ProjectId FROM PublicationEditions WHERE Id = {editionId}),
                 'press-v36', 'Interior', 'complete-fingerprint', {(status == "Rendering" ? (DateTime?)createdAt : null)},
                 {status}, 'Release');
            """);
        if (data is null)
            return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PublicationArtifacts
                (Id, ByteLength, CreatedAt, Data, EditionId, FileName, IsLegacy, Kind, MediaType,
                 PageCount, PaginationFingerprint, ProfileId, ProjectId, RenderJobId,
                 RendererVersion, Sha256, SourceFingerprint, TargetKind)
            VALUES
                ({Guid.NewGuid()}, {data.Length}, {createdAt}, {data}, {editionId}, {fileName}, 0,
                 'InteriorPdf', 'application/pdf', 12, 'pagination', 'fixture',
                 (SELECT ProjectId FROM PublicationEditions WHERE Id = {editionId}),
                 {jobId}, 'press-v36', {hash}, 'complete-fingerprint', 'Release');
            """);
    }
}

using System.Security.Cryptography;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ScopedPublicationRenderingMigrationTests
{
    private const string PreviousMigration = "20260901164016_RemoveCoverBackCopy";

    [Fact]
    public async Task CombinedPhysicalRendersBecomeLegacyWithoutChangingArtifactDataOrDigitalHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "scoped-publication.db")}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var physicalEditionId = Guid.NewGuid();
            var digitalEditionId = Guid.NewGuid();
            var physicalJobId = Guid.NewGuid();
            var digitalJobId = Guid.NewGuid();
            var physicalArtifactId = Guid.NewGuid();
            var digitalArtifactId = Guid.NewGuid();
            var physicalPreparationId = Guid.NewGuid();
            var digitalPreparationId = Guid.NewGuid();
            var physicalBytes = "%PDF-1.7\nphysical-history"u8.ToArray();
            var digitalBytes = "%PDF-1.7\ndigital-history"u8.ToArray();
            var physicalHash = Convert.ToHexStringLower(SHA256.HashData(physicalBytes));
            var digitalHash = Convert.ToHexStringLower(SHA256.HashData(digitalBytes));
            var now = DateTime.UtcNow;

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await DatabaseStartupMigrationService.EnsureCitationCompatibilityColumnsAsync(db, default);
                await DatabaseStartupMigrationService.EnsurePrinterDimensionCompatibilityColumnsAsync(db, default);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Projects
                        (Id, ReviewEditsEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ({projectId}, 1, 0, {now}, 1, 'Scoped publishing', '',
                         {$"scoped-{projectId:N}"}, {now});
                    """);
                db.PublicationEditions.AddRange(
                    new PublicationEdition
                    {
                        Id = physicalEditionId,
                        ProjectId = projectId,
                        Name = "Paperback",
                        Format = PublicationEditionFormat.Paperback,
                        CreatedAt = now,
                        UpdatedAt = now,
                    },
                    new PublicationEdition
                    {
                        Id = digitalEditionId,
                        ProjectId = projectId,
                        Name = "Digital PDF",
                        Format = PublicationEditionFormat.DigitalPdf,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                await db.SaveChangesAsync();
                await InsertLegacyRenderAsync(db, physicalJobId, physicalEditionId, physicalArtifactId,
                    "InteriorPdf", "physical.pdf", physicalBytes, physicalHash, now);
                await InsertLegacyRenderAsync(db, digitalJobId, digitalEditionId, digitalArtifactId,
                    "ReadingPdf", "digital.pdf", digitalBytes, digitalHash, now);
                await InsertLegacyPreparationAsync(db, physicalPreparationId, physicalEditionId, physicalJobId, now);
                await InsertLegacyPreparationAsync(db, digitalPreparationId, digitalEditionId, digitalJobId, now);
                await DatabaseStartupMigrationService.RemoveCitationCompatibilityColumnsAsync(db, default);
                await DatabaseStartupMigrationService.RemovePrinterDimensionCompatibilityColumnsAsync(db, default);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var physicalJob = await db.PublicationRenderJobs.AsNoTracking().SingleAsync(item => item.Id == physicalJobId);
                var digitalJob = await db.PublicationRenderJobs.AsNoTracking().SingleAsync(item => item.Id == digitalJobId);
                var physicalArtifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync(item => item.Id == physicalArtifactId);
                var digitalArtifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync(item => item.Id == digitalArtifactId);
                var physicalPreparation = await db.PublicationPreparationJobs.AsNoTracking().SingleAsync(item => item.Id == physicalPreparationId);
                var digitalPreparation = await db.PublicationPreparationJobs.AsNoTracking().SingleAsync(item => item.Id == digitalPreparationId);

                Assert.Equal(PublicationRenderScope.Book, physicalJob.Scope);
                Assert.True(physicalJob.IsLegacy);
                Assert.True(physicalArtifact.IsLegacy);
                Assert.Equal(physicalBytes, physicalArtifact.Data);
                Assert.Equal(physicalHash, physicalArtifact.Sha256);
                Assert.Null(physicalPreparation.BookRenderJobId);
                Assert.Null(physicalPreparation.InteriorRenderJobId);
                Assert.Null(physicalPreparation.CoverRenderJobId);

                Assert.Equal(PublicationRenderScope.Book, digitalJob.Scope);
                Assert.False(digitalJob.IsLegacy);
                Assert.False(digitalArtifact.IsLegacy);
                Assert.Equal(digitalBytes, digitalArtifact.Data);
                Assert.Equal(digitalHash, digitalArtifact.Sha256);
                Assert.Equal(digitalJobId, digitalPreparation.BookRenderJobId);
                Assert.Null(digitalPreparation.InteriorRenderJobId);
                Assert.Null(digitalPreparation.CoverRenderJobId);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<int> InsertLegacyRenderAsync(
        AppDbContext db,
        Guid jobId,
        Guid editionId,
        Guid artifactId,
        string kind,
        string fileName,
        byte[] data,
        string hash,
        DateTime now) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PublicationRenderJobs
                (Id, CancellationRequested, CompletedAt, CreatedAt, DiagnosticsJson, EditionId,
                 EvidenceJson, IsLegacy, PaginationFingerprint, ProfileId, ProgressMessage,
                 ProgressPercent, ProjectId, RendererVersion, SourceFingerprint, StartedAt,
                 Status, TargetKind)
            VALUES
                ({jobId}, 0, {now}, {now}, '[]', {editionId}, json_object(), 0, 'pagination',
                 'fixture', 'Completed', 100,
                 (SELECT ProjectId FROM PublicationEditions WHERE Id = {editionId}),
                 'press-v11', 'complete-fingerprint', {now}, 'Completed', 'Release');

            INSERT INTO PublicationArtifacts
                (Id, ByteLength, CreatedAt, Data, EditionId, FileName, IsLegacy, Kind, MediaType,
                 PageCount, PaginationFingerprint, ProfileId, ProjectId, RenderJobId,
                 RendererVersion, Sha256, SourceFingerprint, TargetKind)
            VALUES
                ({artifactId}, {data.Length}, {now}, {data}, {editionId}, {fileName}, 0, {kind},
                 'application/pdf', 12, 'pagination', 'fixture',
                 (SELECT ProjectId FROM PublicationEditions WHERE Id = {editionId}),
                 {jobId}, 'press-v11', {hash}, 'complete-fingerprint', 'Release');
            """);

    private static Task<int> InsertLegacyPreparationAsync(
        AppDbContext db,
        Guid preparationId,
        Guid editionId,
        Guid renderJobId,
        DateTime now) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PublicationPreparationJobs
                (Id, ProjectId, TargetKind, EditionId, RenderJobId, Status, Step, ProgressPercent,
                 Message, DiagnosticsJson, SourceFingerprint, CancellationRequested, CreatedAt,
                 StartedAt, CompletedAt, ImagePreparationSummaryJson)
            VALUES
                ({preparationId},
                 (SELECT ProjectId FROM PublicationEditions WHERE Id = {editionId}),
                 'Release', {editionId}, {renderJobId}, 'Ready', 'Complete', 100, 'Ready', '[]',
                 'complete-fingerprint', 0, {now}, {now}, {now}, json_object());
            """);
}

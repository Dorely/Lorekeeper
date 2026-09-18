using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Tokens;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Tests;

// Retained-source migration preservation: no provider, worker, or browser simulation.
public sealed class SourceRetentionConversionTests
{
    [Fact]
    public async Task ConversionPreservesLegacyTextEvidenceAndUnavailableOriginalAcrossRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Service.QueueLegacyConversionAsync(fixture.ProjectId, fixture.SourceId);
        var duplicate = await fixture.Service.QueueLegacyConversionAsync(fixture.ProjectId, fixture.SourceId);
        Assert.Equal(job.Id, duplicate.Id);
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Running);

        var converted = await fixture.Service.ConvertLegacySourceAsync(job.Id);
        var retry = await fixture.Service.ConvertLegacySourceAsync(job.Id);
        Assert.Equal(job.Id, converted.ActiveExtractionVersionId);
        Assert.Equal(converted.ActiveExtractionVersionId, retry.ActiveExtractionVersionId);
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Completed);
        await fixture.Service.RestartAsync(job.Id);
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Running);
        await fixture.Service.ConvertLegacySourceAsync(job.Id);
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Completed);
        await fixture.Service.DeleteJobAsync(job.Id);
        await using var db = fixture.Factory.CreateDbContext();
        var versions = await db.SourceExtractionVersions.OrderBy(item => item.Ordinal).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(fixture.SourceId, versions[0].Id);
        Assert.Equal(SourceExtractionStatus.LegacyImmutable, versions[0].Status);
        Assert.Equal("legacy-hash", versions[0].ContentHash);
        Assert.Equal(Fixture.LegacyText, versions[0].NormalizedText);
        Assert.Equal(SourceExtractionStatus.Ready, versions[1].Status);
        Assert.Equal(Fixture.LegacyText, versions[1].NormalizedText);
        Assert.Equal(SourceRetentionValidator.Sha256(Fixture.LegacyText), versions[1].ContentHash);
        Assert.Equal(fixture.SourceId, (await db.SourceLocations.SingleAsync()).ExtractionVersionId);
        Assert.Equal("Saved", (await db.SourceLocations.SingleAsync()).Quote);
        var original = await db.SourceOriginals.Include(item => item.Chunks).SingleAsync();
        Assert.Equal(SourceOriginalState.OriginalUnavailable, original.State);
        Assert.Null(original.Sha256);
        Assert.Empty(original.Chunks);
        Assert.Empty(await db.SourceOriginalBlobs.ToListAsync());
        var blocks = await db.IngestSourceBlocks.Where(item => item.SourceExtractionVersionId == job.Id).OrderBy(item => item.Index).ToListAsync();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block =>
        {
            Assert.Equal(Fixture.LegacyText[block.StartChar..block.EndChar], block.NormalizedText);
            Assert.Equal(SourceRetentionValidator.Sha256(block.NormalizedText), block.ContentHash);
        });
        Assert.Empty(await db.IngestJobChunks.ToListAsync());
        Assert.Empty(await db.IngestStagingRecords.ToListAsync());
    }

    [Fact]
    public async Task StoppedConversionPublishesNothingAndCanResumeWithoutChatConfiguration()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.Service.QueueLegacyConversionAsync(fixture.ProjectId, fixture.SourceId);
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Stopped);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.ConvertLegacySourceAsync(job.Id));
        await using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.Single(await db.SourceExtractionVersions.ToListAsync());
            Assert.Empty(await db.IngestSourceBlocks.ToListAsync());
            Assert.Equal(fixture.SourceId, (await db.IngestSources.SingleAsync()).ActiveExtractionVersionId);
        }
        await fixture.Service.ResumeAsync(job.Id);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var resumed = await db.IngestJobs.SingleAsync();
            Assert.Equal(IngestJobStatus.Queued, resumed.Status);
            Assert.Null(resumed.ProviderId);
        }
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Running);
        await fixture.Service.ConvertLegacySourceAsync(job.Id);
    }

    [Fact]
    public async Task ConversionRejectsForeignProjectAndChangedActiveExtraction()
    {
        await using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.QueueLegacyConversionAsync(Guid.NewGuid(), fixture.SourceId));
        var job = await fixture.Service.QueueLegacyConversionAsync(fixture.ProjectId, fixture.SourceId);
        await fixture.SetStatusAsync(job.Id, IngestJobStatus.Running);
        await using (var db = fixture.Factory.CreateDbContext())
        {
            await db.IngestSources.Where(item => item.Id == fixture.SourceId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ActiveExtractionVersionId, (Guid?)null));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConvertLegacySourceAsync(job.Id));
        await using var verify = fixture.Factory.CreateDbContext();
        Assert.Single(await verify.SourceExtractionVersions.ToListAsync());
        Assert.Empty(await verify.IngestSourceBlocks.ToListAsync());
    }

    [Fact]
    public async Task CopiedPredecessorJobsKeepEntityExtractionModeAndCheckpoints()
    {
        await using var predecessor = new SqliteConnection("Data Source=:memory:");
        await using var copy = new SqliteConnection("Data Source=:memory:");
        await predecessor.OpenAsync();
        await copy.OpenAsync();
        var factory = new ContextFactory(predecessor);
        var projectId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260918190000_RemoveSupersededBibliographyAccessTimestamp");
            db.Projects.Add(new Project { Id = projectId, Name = "Predecessor", Slug = "predecessor" });
            db.IngestSources.Add(new IngestSource { Id = sourceId, ProjectId = projectId, Title = "Prior source", UserInstructions = "Keep instructions" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO IngestJobs (Id, ProjectId, SourceId, Instructions, Status, TotalSourceChunks,
                    CompletedSourceChunks, CreatedEntityCount, CreatedRelationshipCount, CreatedAt, UpdatedAt)
                VALUES ({{jobId}}, {{projectId}}, {{sourceId}}, 'Keep instructions', 'Stopped', 4, 2, 7, 3, {{now}}, {{now}})
                """);
        }
        predecessor.BackupDatabase(copy);
        await using (var db = new ContextFactory(copy).CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var job = await db.IngestJobs.SingleAsync();
            Assert.Equal(IngestJobMode.ExtractEntities, job.Mode);
            Assert.Null(job.SourceExtractionVersionId);
            Assert.Equal(IngestJobStatus.Stopped, job.Status);
            Assert.Equal(2, job.CompletedSourceChunks);
            Assert.Equal(7, job.CreatedEntityCount);
            Assert.Equal(3, job.CreatedRelationshipCount);
            Assert.Equal("Keep instructions", job.Instructions);
            Assert.Equal(jobId, job.Id);
        }
        await using var command = predecessor.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('IngestJobs') WHERE name = 'Mode'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private sealed class ContextFactory(SqliteConnection connection) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options,
            NullLogger<AppDbContext>.Instance);
    }

    private sealed class Fixture(SqliteConnection connection) : IAsyncDisposable
    {
        public const string LegacyText = "Saved legacy text.\r\n\r\n# Another section\r\nExact text and evidence must survive.";
        public ContextFactory Factory { get; } = new(connection);
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid SourceId { get; } = Guid.NewGuid();
        public IngestService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var fixture = new Fixture(connection);
            await using (var db = fixture.Factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                db.Projects.Add(new Project { Id = fixture.ProjectId, Name = "Legacy conversion", Slug = "legacy-conversion" });
                db.IngestSources.Add(new IngestSource { Id = fixture.SourceId, ProjectId = fixture.ProjectId,
                    Title = "Legacy source", UserInstructions = "", ActiveExtractionVersionId = fixture.SourceId });
                db.SourceOriginals.Add(new SourceOriginal { SourceId = fixture.SourceId, State = SourceOriginalState.OriginalUnavailable,
                    FileName = "legacy.txt", MediaType = "text/plain" });
                db.SourceExtractionVersions.Add(new SourceExtractionVersion { Id = fixture.SourceId, SourceId = fixture.SourceId,
                    Extractor = "legacy", ExtractorVersion = "1", Ordinal = 0, Status = SourceExtractionStatus.LegacyImmutable,
                    NormalizedText = LegacyText, ContentHash = "legacy-hash" });
                db.SourceLocations.Add(new SourceLocation { ProjectId = fixture.ProjectId, SourceId = fixture.SourceId,
                    ExtractionVersionId = fixture.SourceId, NormalizedStart = 0, NormalizedLength = 5,
                    Quote = "Saved", VerificationHash = SourceRetentionValidator.Sha256("Saved") });
                await db.SaveChangesAsync();
            }
            var tokenOptions = Options.Create(new TokenCountingOptions());
            var tokenCounter = new CompositeTokenCounter(new TiktokenTokenCounter(tokenOptions),
                new CharEstimateTokenCounter(tokenOptions), NullLogger<CompositeTokenCounter>.Instance);
            fixture.Service = new IngestService(
                new AppDatabaseOperationFactory(fixture.Factory, new AppDatabaseWriteCoordinator(), new ProjectMutationCoordinator()),
                new IngestSourceStructureBuilder(tokenCounter, Options.Create(new IngestSourceStructureOptions())),
                null!, null!, null!, new IngestJobQueue(), null!, new IngestJobNotifier(), null!, null!, null!, null!,
                Options.Create(new BookArtifactIngestOptions()), NullLogger<IngestService>.Instance);
            return fixture;
        }

        public async Task SetStatusAsync(Guid jobId, IngestJobStatus status)
        {
            await using var db = Factory.CreateDbContext();
            await db.IngestJobs.Where(item => item.Id == jobId).ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, status));
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

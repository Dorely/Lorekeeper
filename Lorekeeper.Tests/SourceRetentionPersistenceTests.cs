using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class SourceRetentionPersistenceTests
{
    private const string PreviousMigration = "20260917203529_AuthoringBatchJournalV37";

    [Fact]
    public void AvailableOriginalUsesBoundedContentAddressedChunksAndIncrementalHashValidation()
    {
        var bytes = new byte[SourceOriginal.MaximumChunkBytes + 1];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(index % 251);
        var blobs = new Dictionary<string, SourceOriginalBlob>(StringComparer.Ordinal);

        var first = SourceRetentionValidator.BuildAvailableOriginal(
            Guid.NewGuid(), "fixture.bin", "application/octet-stream", bytes, blobs);
        var second = SourceRetentionValidator.BuildAvailableOriginal(
            Guid.NewGuid(), "fixture-copy.bin", "application/octet-stream", bytes, blobs);

        Assert.Equal(2, first.Chunks.Count);
        Assert.Equal(SourceOriginal.MaximumChunkBytes, first.Chunks.Single(chunk => chunk.Index == 0).ByteLength);
        Assert.Equal(1, first.Chunks.Single(chunk => chunk.Index == 1).ByteLength);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(2, blobs.Count);
        SourceRetentionValidator.ValidateOriginal(first);
    }

    [Fact]
    public void SourceLocationValidationFailsClosedForMismatchedQuoteOrBlockOwnership()
    {
        var sourceId = Guid.NewGuid();
        var extraction = new SourceExtractionVersion
        {
            SourceId = sourceId,
            Ordinal = 0,
            Extractor = "fixture",
            ExtractorVersion = "1",
            ContentHash = SourceRetentionValidator.Sha256("Alpha beta"),
            Status = SourceExtractionStatus.Ready,
            NormalizedText = "Alpha beta",
        };
        var block = new IngestSourceBlock
        {
            SourceId = sourceId,
            SourceExtractionVersionId = extraction.Id,
            Index = 0,
            StartChar = 0,
            EndChar = extraction.NormalizedText.Length,
        };
        var location = new SourceLocation
        {
            ProjectId = Guid.NewGuid(),
            SourceId = sourceId,
            ExtractionVersionId = extraction.Id,
            SourceBlockId = block.Id,
            NormalizedStart = 0,
            NormalizedLength = 5,
            Quote = "Alpha",
            VerificationHash = SourceRetentionValidator.Sha256("Alpha"),
        };

        SourceRetentionValidator.ValidateLocation(location, extraction, block);

        location.Quote = "Wrong";
        Assert.Throws<InvalidOperationException>(() => SourceRetentionValidator.ValidateLocation(location, extraction, block));
        location.Quote = "Alpha";
        location.SourceBlockId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => SourceRetentionValidator.ValidateLocation(location, extraction, block));
    }

    [Fact]
    public async Task BibliographicRecordCanExistWithoutAnIngestSource()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        await db.Database.EnsureCreatedAsync();
        var project = new Project { Name = "Bibliography fixture", Slug = "bibliography-fixture" };
        db.Projects.Add(project);
        db.BibliographicRecords.Add(new BibliographicRecord
        {
            ProjectId = project.Id,
            Title = "Independent work",
            Kind = BibliographicRecordKind.Book,
        });

        await db.SaveChangesAsync();

        var record = await db.BibliographicRecords.AsNoTracking().SingleAsync();
        Assert.Null(record.SourceId);
        Assert.Equal("Independent work", record.Title);
    }

    [Fact]
    public async Task LegacySourceMigratesToUnavailableOriginalWithoutSynthesizingBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "retained-source.db")}")
                .Options;
            var projectId = Guid.NewGuid();
            var sourceId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO Projects
                        (Id, ReviewEditsEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ({{projectId}}, 1, 0, {{now}}, 1,
                         {{"Legacy source fixture"}}, {{string.Empty}}, {{$"legacy-source-{projectId:N}"}}, {{now}})
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO IngestSources
                        (Id, ProjectId, Title, SourceKind, Description, UserInstructions,
                         SourceText, SourceHash, VectorIndexState, CreatedAt, UpdatedAt)
                    VALUES
                        ({{sourceId}}, {{projectId}}, {{"Legacy source"}}, {{"Text"}}, {{string.Empty}}, {{string.Empty}},
                         {{"Retain this legacy extraction exactly."}}, {{"legacy-text-hash"}}, {{"Stale"}}, {{now}}, {{now}})
                    """);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var original = await db.SourceOriginals.Include(item => item.Chunks).SingleAsync(item => item.SourceId == sourceId);
                var extraction = await db.SourceExtractionVersions.SingleAsync(item => item.SourceId == sourceId);
                Assert.Equal(SourceOriginalState.OriginalUnavailable, original.State);
                Assert.Equal(0, original.Length);
                Assert.Null(original.Sha256);
                Assert.Empty(original.Chunks);
                Assert.Empty(await db.SourceOriginalBlobs.ToListAsync());
                Assert.Equal(sourceId, extraction.Id);
                Assert.Equal(SourceExtractionStatus.LegacyImmutable, extraction.Status);
                Assert.Equal("legacy-text-hash", extraction.ContentHash);
                Assert.Equal("Retain this legacy extraction exactly.", extraction.NormalizedText);
                Assert.Equal(sourceId, (await db.IngestSources.SingleAsync(item => item.Id == sourceId)).ActiveExtractionVersionId);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT SourceText, SourceHash FROM IngestSources WHERE Id = $id";
                var id = command.CreateParameter();
                id.ParameterName = "$id";
                id.Value = sourceId;
                command.Parameters.Add(id);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Retain this legacy extraction exactly.", reader.GetString(0));
                Assert.Equal("legacy-text-hash", reader.GetString(1));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

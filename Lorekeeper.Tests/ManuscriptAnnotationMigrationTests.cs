using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ManuscriptAnnotationMigrationTests
{
    private const string PreviousMigration = "20260818064325_AddLlmProviderMaxTokens";
    private const string AuthoringHistoryPreviousMigration = "20260820230218_AddLatestAssistantReviewBaseline";

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);
    }

    private sealed class NoopManuscriptService : IManuscriptService
    {
        public Task<ManuscriptSnapshot?> GetManuscriptAsync(
            EditorContentTarget target,
            Guid chapterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ManuscriptSnapshot?>(null);

        public Task<ManuscriptMutationResult> ReplaceDocumentAsync(
            EditorContentTarget target,
            Guid chapterId,
            long expectedRevision,
            ManuscriptDocument document,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ManuscriptMutationResult>(new NotSupportedException());

        public Task<ManuscriptMutationResult> ApplyAsync(
            EditorContentTarget target,
            Guid chapterId,
            long expectedRevision,
            IReadOnlyList<ManuscriptOperation> operations,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ManuscriptMutationResult>(new NotSupportedException());

        public Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
            EditorContentTarget target,
            Guid chapterId,
            long expectedRevision,
            IReadOnlyList<ManuscriptOperation> operations,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ManuscriptMutationResult>(new NotSupportedException());

        public Task ValidateDocumentReferencesAsync(
            EditorContentTarget target,
            Guid chapterId,
            ManuscriptDocument document,
            IReadOnlyList<ManuscriptStyleView>? styleCatalog = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task AdditiveMigrationPreservesProjectManuscriptPublicationAndArtifactData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "annotations.db")}")
                .Options;
            var projectId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var manuscript = new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Revision = 7,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "body",
                        Type = ManuscriptBlockType.Paragraph,
                        Content = [new ManuscriptInline { Text = "Protected manuscript Ω" }],
                    },
                ],
            };

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await DatabaseStartupMigrationService.EnsureRectoChapterStartsCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await DatabaseStartupMigrationService.EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await DatabaseStartupMigrationService.EnsurePrintProductCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await LegacyProjectSeed.InsertAsync(
                    db,
                    projectId,
                    "Protected project",
                    $"protected-{projectId:N}");
                db.Chapters.Add(new Chapter
                {
                    Id = chapterId,
                    ProjectId = projectId,
                    Title = "Protected chapter",
                    ManuscriptJson = ManuscriptCodec.Serialize(manuscript),
                    ManuscriptRevision = manuscript.Revision,
                });
                db.PublicationEditions.Add(new PublicationEdition
                {
                    Id = editionId,
                    ProjectId = projectId,
                    Name = "Protected edition",
                });
                db.PublicationArtifacts.Add(new PublicationArtifact
                {
                    Id = artifactId,
                    ProjectId = projectId,
                    EditionId = editionId,
                    Kind = PublicationArtifactKind.ReadingPdf,
                    FileName = "protected.pdf",
                    MediaType = "application/pdf",
                    Data = [1, 2, 3, 4],
                    Sha256 = "preserved",
                    ByteLength = 4,
                    SourceFingerprint = "source-fingerprint",
                    PaginationFingerprint = "pagination-fingerprint",
                    RendererVersion = "test",
                    ProfileId = "preview-1",
                });
                await db.SaveChangesAsync();
                await DatabaseStartupMigrationService.RemoveRectoChapterStartsCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await DatabaseStartupMigrationService.RemoveBarnesAndNoblePrintCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await DatabaseStartupMigrationService.RemovePrintArtifactProfileCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal("Protected project", (await db.Projects.AsNoTracking().SingleAsync()).Name);
                var chapter = await db.Chapters.AsNoTracking().SingleAsync();
                Assert.Equal(7, chapter.ManuscriptRevision);
                Assert.Equal("Protected manuscript Ω", ManuscriptCodec.ProjectPlainText(chapter.Manuscript));
                Assert.Equal("Protected edition", (await db.PublicationEditions.AsNoTracking().SingleAsync()).Name);
                var artifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync();
                Assert.Equal(artifactId, artifact.Id);
                Assert.Equal([1, 2, 3, 4], artifact.Data);
                Assert.Equal("source-fingerprint", artifact.SourceFingerprint);
                Assert.Empty(await db.ManuscriptAnnotations.AsNoTracking().ToListAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthoringHistoryCleanupDropsRetiredReviewBaselines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "review-baseline.db")}")
                .Options;
            var projectId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var durableOnlyChapterId = Guid.NewGuid();
            var durableStreamId = Guid.NewGuid();
            var durableOnlyStreamId = Guid.NewGuid();
            var legacyStreamId = Guid.NewGuid();
            var batchId = Guid.NewGuid();
            var durableEntryId = Guid.NewGuid();
            var legacyEntryId = Guid.NewGuid();
            var durableTurnId = Guid.NewGuid();
            var durableOnlyTurnId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var manuscript = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Revision = 4,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "body",
                        Type = ManuscriptBlockType.Paragraph,
                        Content = [new ManuscriptInline { Text = "Preserved review-baseline manuscript" }],
                    },
                ],
            });
            var afterManuscript = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Revision = 5,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "body",
                        Type = ManuscriptBlockType.Paragraph,
                        Content = [new ManuscriptInline { Text = "After assistant mutation" }],
                    },
                ],
            });
            var durableOnlyManuscript = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = durableOnlyChapterId,
                Revision = 2,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "body",
                        Type = ManuscriptBlockType.Paragraph,
                        Content = [new ManuscriptInline { Text = "Durable review columns remain intact" }],
                    },
                ],
            });
            var legacyBeforeManuscript = ManuscriptCodec.Serialize(new ManuscriptDocument
            {
                ManuscriptId = chapterId,
                Revision = 3,
                Content =
                [
                    new ManuscriptBlock
                    {
                        Id = "body",
                        Type = ManuscriptBlockType.Paragraph,
                        Content = [new ManuscriptInline { Text = "Legacy before committed assistant mutation" }],
                    },
                ],
            });
            var baselineBytes = CompressSnapshot(legacyBeforeManuscript);
            var resultBytes = CompressSnapshot(afterManuscript);
            var manuscriptHash = Hash(manuscript);
            var legacyBeforeHash = Hash(legacyBeforeManuscript);
            var durableOnlyHash = Hash(durableOnlyManuscript);

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(AuthoringHistoryPreviousMigration);
                await DatabaseStartupMigrationService.EnsureRectoChapterStartsCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await DatabaseStartupMigrationService.EnsureBarnesAndNoblePrintCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await DatabaseStartupMigrationService.EnsurePrintProductCompatibilityColumnsAsync(
                    db,
                    CancellationToken.None);
                await LegacyProjectSeed.InsertAsync(
                    db,
                    projectId,
                    "Review baseline fixture",
                    $"review-baseline-{projectId:N}");
                db.Chapters.Add(new Chapter
                {
                    Id = chapterId,
                    ProjectId = projectId,
                    Title = "Preserved chapter",
                    ManuscriptJson = manuscript,
                    ManuscriptRevision = 4,
                });
                db.Chapters.Add(new Chapter
                {
                    Id = durableOnlyChapterId,
                    ProjectId = projectId,
                    Title = "Durable baseline chapter",
                    ManuscriptJson = durableOnlyManuscript,
                    ManuscriptRevision = 2,
                });
                db.PublicationEditions.Add(new PublicationEdition
                {
                    Id = editionId,
                    ProjectId = projectId,
                    Name = "Review baseline release",
                });
                await db.SaveChangesAsync();
                db.ManuscriptMigrationJournals.AddRange(
                    new[]
                    {
                        ManuscriptMigrationService.MigrationName,
                        ManuscriptMigrationService.SchemaV2MigrationName,
                        ManuscriptMigrationService.SchemaV3MigrationName,
                        VisualCompositionMigrationService.MigrationName,
                        VisualCompositionMigrationService.GeometryPolicyMigrationName,
                        VisualCompositionMigrationService.AccessibilityDecisionMigrationName,
                        AuthoringPageMigrationService.MigrationName,
                        EditionContentMigrationService.MigrationName,
                        PublicationCoreMigrationService.MigrationName,
                        PublicationSectionMigrationService.MigrationName,
                    }.Select(name => new ManuscriptMigrationJournal
                    {
                        MigrationName = name,
                        Phase = ManuscriptMigrationPhase.Complete,
                        Status = ManuscriptMigrationStatus.Completed,
                        CompletedAt = now,
                    }));
                db.PublicationEditionMigrationJournals.AddRange(
                    new[]
                    {
                        PublicationEditionMigrationService.MigrationName,
                        PublicationPressMigrationService.MigrationName,
                        PrintArtifactProfileMigrationService.MigrationName,
                    }.Select(name => new PublicationEditionMigrationJournal
                    {
                        MigrationName = name,
                        Status = "Completed",
                        CompletedAt = now,
                    }));
                await db.SaveChangesAsync();

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryStreams
                        (Id, ProjectId, StreamKey, DocumentKind, DocumentId, EditionId,
                         BaselineSnapshot, BaselineHash, FirstSequence, LastSequence,
                         CursorSequence, Revision, CreatedAt, UpdatedAt,
                         LatestReviewBeforeJson, LatestReviewBeforeHash,
                         LatestReviewAssistantTurnId, LatestReviewActionLabel,
                         LatestReviewCapturedAt)
                    VALUES
                        ({durableStreamId}, {projectId}, {"core:chapter:" + chapterId.ToString("D")}, {"CoreChapter"}, {chapterId}, NULL,
                         {baselineBytes}, {"baseline-hash"}, 1, 2, 2, 3, {now}, {now},
                         {manuscript}, {manuscriptHash}, {durableTurnId}, {"Durable review"}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryEntries
                        (Id, StreamId, Sequence, ActionLabel, Origin, AssistantTurnId,
                         ResultSnapshot, ResultHash, SelectionJson, CreatedAt)
                    VALUES
                        ({durableEntryId}, {durableStreamId}, 2, {"Newer committed review"}, {"Assistant"}, {durableTurnId},
                         {resultBytes}, {"result-hash"}, {""}, {now.AddMinutes(1)});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryStreams
                        (Id, ProjectId, StreamKey, DocumentKind, DocumentId, EditionId,
                         BaselineSnapshot, BaselineHash, FirstSequence, LastSequence,
                         CursorSequence, Revision, CreatedAt, UpdatedAt,
                         LatestReviewBeforeJson, LatestReviewBeforeHash,
                         LatestReviewAssistantTurnId, LatestReviewActionLabel,
                         LatestReviewCapturedAt)
                    VALUES
                        ({durableOnlyStreamId}, {projectId}, {"core:chapter:" + durableOnlyChapterId.ToString("D")}, {"CoreChapter"}, {durableOnlyChapterId}, NULL,
                         {CompressSnapshot(durableOnlyManuscript)}, {"baseline-hash"}, 1, 1, 1, 2, {now}, {now},
                         {durableOnlyManuscript}, {durableOnlyHash}, {durableOnlyTurnId}, {"Durable columns"}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryStreams
                        (Id, ProjectId, StreamKey, DocumentKind, DocumentId, EditionId,
                         BaselineSnapshot, BaselineHash, FirstSequence, LastSequence,
                         CursorSequence, Revision, CreatedAt, UpdatedAt)
                    VALUES
                        ({legacyStreamId}, {projectId}, {"release:" + editionId.ToString("D") + ":chapter:" + chapterId.ToString("D")}, {"EditionChapter"}, {chapterId}, {editionId},
                         {baselineBytes}, {"baseline-hash"}, 1, 1, 1, 2, {now}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryEntries
                        (Id, StreamId, Sequence, ActionLabel, Origin, AssistantTurnId,
                         ResultSnapshot, ResultHash, SelectionJson, CreatedAt)
                    VALUES
                        ({legacyEntryId}, {legacyStreamId}, 1, {"Assistant change"}, {"Assistant"}, NULL,
                         {resultBytes}, {"result-hash"}, {""}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringTurnHistoryBatches
                        (Id, StreamId, AssistantTurnId, ActionLabel, BeforeSnapshot,
                         BeforeHash, AfterSnapshot, AfterHash, SelectionJson, Status,
                         CreatedAt, UpdatedAt, FinalizedAt)
                    VALUES
                        ({batchId}, {legacyStreamId}, {Guid.NewGuid()}, {"Open assistant change"},
                         {baselineBytes}, {"before-hash"}, {resultBytes}, {"after-hash"},
                         {""}, {"Open"}, {now}, {now}, NULL);
                    """);
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={Path.Combine(directory, "review-baseline.db")}",
                })
                .Build();
            var recovery = new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance);
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator());
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var startupMigration = new DatabaseStartupMigrationService(
                    database,
                    new ManuscriptMigrationService(
                        configuration,
                        recovery,
                        NullLogger<ManuscriptMigrationService>.Instance),
                    new PublicationEditionMigrationService(
                        configuration,
                        recovery,
                        NullLogger<PublicationEditionMigrationService>.Instance),
                    new PublicationPressMigrationService(
                        configuration,
                        recovery,
                        NullLogger<PublicationPressMigrationService>.Instance),
                    new VisualCompositionMigrationService(
                        recovery,
                        NullLogger<VisualCompositionMigrationService>.Instance),
                    new AuthoringPageMigrationService(
                        recovery,
                        NullLogger<AuthoringPageMigrationService>.Instance),
                    new PublicationCoreMigrationService(
                        database,
                        recovery,
                        NullLogger<PublicationCoreMigrationService>.Instance),
                    new EditionContentMigrationService(
                        recovery,
                        new NoopManuscriptService(),
                        NullLogger<EditionContentMigrationService>.Instance),
                    new PublicationSectionMigrationService(
                        recovery,
                        NullLogger<PublicationSectionMigrationService>.Instance),
                    new DesignedPageMigrationService(),
                    new PrintArtifactProfileMigrationService(
                        recovery,
                        new PrintArtifactProfileRegistry(),
                        NullLogger<PrintArtifactProfileMigrationService>.Instance),
                    recovery);
                Assert.True(await startupMigration.ApplyAsync(), (await recovery.GetStateAsync()).Error);

                var unplacedPageId = Guid.NewGuid();
                var unplacedVariantId = Guid.NewGuid();
                var retainedPageId = Guid.NewGuid();
                var retainedVariantId = Guid.NewGuid();
                var sceneJson = JsonSerializer.Serialize(new CompositionScene(), ManuscriptCodec.JsonOptions);
                db.DesignedPages.AddRange(
                    new DesignedPage
                    {
                        Id = unplacedPageId,
                        ProjectId = projectId,
                        Name = "Unplaced library page",
                    },
                    new DesignedPage
                    {
                        Id = retainedPageId,
                        ProjectId = projectId,
                        Name = "Retained page with authored layout",
                    });
                db.DesignedPageContents.AddRange(
                    new DesignedPageContent
                    {
                        Id = unplacedPageId,
                        ProjectId = projectId,
                        DesignedPageId = unplacedPageId,
                        SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(unplacedPageId, 1)),
                        ActiveVariantId = unplacedVariantId,
                        Revision = 1,
                    },
                    new DesignedPageContent
                    {
                        Id = retainedPageId,
                        ProjectId = projectId,
                        DesignedPageId = retainedPageId,
                        SemanticManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(retainedPageId, 1)),
                        ActiveVariantId = retainedVariantId,
                        Revision = 1,
                    });
                db.DesignedPageVariants.AddRange(
                    new DesignedPageVariant
                    {
                        Id = unplacedVariantId,
                        ContentId = unplacedPageId,
                        GeometryKey = "unplaced",
                        SceneJson = sceneJson,
                        Revision = 1,
                    },
                    new DesignedPageVariant
                    {
                        Id = retainedVariantId,
                        ContentId = retainedPageId,
                        GeometryKey = "retained",
                        SceneJson = sceneJson,
                        Revision = 1,
                    });
                await db.SaveChangesAsync();

                Assert.True(await startupMigration.ApplyAsync(), (await recovery.GetStateAsync()).Error);
                db.ChangeTracker.Clear();
                Assert.NotNull(await db.DesignedPageVariants.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == unplacedVariantId));
                Assert.NotNull(await db.DesignedPages.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == unplacedPageId));
                Assert.NotNull(await db.DesignedPageVariants.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == retainedVariantId));
                Assert.NotNull(await db.DesignedPages.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == retainedPageId));
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal("Review baseline fixture", (await db.Projects.AsNoTracking().SingleAsync()).Name);
                var chapter = await db.Chapters.AsNoTracking().SingleAsync(item => item.Id == chapterId);
                Assert.Equal(4, chapter.ManuscriptRevision);
                Assert.Equal(manuscript, chapter.ManuscriptJson);
                Assert.Null(await db.Database.SqlQueryRaw<string>(
                    "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'AssistantReviewBaselines'").FirstOrDefaultAsync());
                Assert.Null(await db.Database.SqlQueryRaw<string>(
                    "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'AuthoringHistory%'").FirstOrDefaultAsync());
                Assert.Null(await db.Database.SqlQueryRaw<string>(
                    "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'AuthoringTurnHistoryBatches'").FirstOrDefaultAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CompressSnapshot(string manuscriptJson)
    {
        var payload = JsonSerializer.Serialize(new { manuscriptJson, compositions = Array.Empty<object>(), inherited = false });
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new StreamWriter(brotli, Encoding.UTF8, leaveOpen: true))
                writer.Write(payload);
        return output.ToArray();
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ManuscriptAnnotationMigrationTests
{
    private const string PreviousMigration = "20260818064325_AddLlmProviderMaxTokens";
    private const string AuthoringHistoryPreviousMigration = "20260820130000_AddChatConversationModelSelection";

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
                db.Projects.Add(new Project { Id = projectId, Name = "Protected project", Slug = $"protected-{projectId:N}" });
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
    public async Task LatestReviewBaselineMigrationPreservesAuthoringHistoryAndInitializesNullableFields()
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
            var streamId = Guid.NewGuid();
            var batchId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var baselineBytes = new byte[] { 1, 2, 3 };
            var resultBytes = new byte[] { 4, 5, 6 };
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

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(AuthoringHistoryPreviousMigration);
                db.Projects.Add(new Project { Id = projectId, Name = "Review baseline fixture", Slug = $"review-baseline-{projectId:N}" });
                db.Chapters.Add(new Chapter
                {
                    Id = chapterId,
                    ProjectId = projectId,
                    Title = "Preserved chapter",
                    ManuscriptJson = manuscript,
                    ManuscriptRevision = 4,
                });
                await db.SaveChangesAsync();

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryStreams
                        (Id, ProjectId, StreamKey, DocumentKind, DocumentId, EditionId,
                         BaselineSnapshot, BaselineHash, FirstSequence, LastSequence,
                         CursorSequence, Revision, CreatedAt, UpdatedAt)
                    VALUES
                        ({streamId}, {projectId}, {"core:chapter:" + chapterId.ToString("D")}, {"CoreChapter"}, {chapterId}, NULL,
                         {baselineBytes}, {"baseline-hash"}, 1, 1, 1, 2, {now}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringHistoryEntries
                        (Id, StreamId, Sequence, ActionLabel, Origin, AssistantTurnId,
                         ResultSnapshot, ResultHash, SelectionJson, CreatedAt)
                    VALUES
                        ({entryId}, {streamId}, 1, {"Assistant change"}, {"Assistant"}, NULL,
                         {resultBytes}, {"result-hash"}, {""}, {now});
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AuthoringTurnHistoryBatches
                        (Id, StreamId, AssistantTurnId, ActionLabel, BeforeSnapshot,
                         BeforeHash, AfterSnapshot, AfterHash, SelectionJson, Status,
                         CreatedAt, UpdatedAt, FinalizedAt)
                    VALUES
                        ({batchId}, {streamId}, {Guid.NewGuid()}, {"Assistant change"},
                         {baselineBytes}, {"before-hash"}, {resultBytes}, {"after-hash"},
                         {""}, {"Completed"}, {now}, {now}, {now});
                    """);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Equal("Review baseline fixture", (await db.Projects.AsNoTracking().SingleAsync()).Name);
                var chapter = await db.Chapters.AsNoTracking().SingleAsync(item => item.Id == chapterId);
                Assert.Equal(4, chapter.ManuscriptRevision);
                Assert.Equal(manuscript, chapter.ManuscriptJson);
                var stream = await db.AuthoringHistoryStreams.AsNoTracking().SingleAsync(item => item.Id == streamId);
                Assert.Equal(baselineBytes, stream.BaselineSnapshot);
                Assert.Null(stream.LatestReviewBeforeJson);
                Assert.Null(stream.LatestReviewBeforeHash);
                Assert.Null(stream.LatestReviewAssistantTurnId);
                Assert.Null(stream.LatestReviewActionLabel);
                Assert.Null(stream.LatestReviewCapturedAt);
                Assert.Equal(resultBytes, (await db.AuthoringHistoryEntries.AsNoTracking().SingleAsync()).ResultSnapshot);
                var batch = await db.AuthoringTurnHistoryBatches.AsNoTracking().SingleAsync(item => item.Id == batchId);
                Assert.Equal(resultBytes, batch.AfterSnapshot);
                Assert.Null(batch.ReviewBaselineManuscriptJson);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

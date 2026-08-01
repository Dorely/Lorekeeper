using Lorekeeper.Models;
using Lorekeeper.Manuscripts;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class PublishConversationMigrationTests
{
    private const string PreviousMigration = "20260730213800_PublicationCoverDesignV12";

    [Fact]
    public async Task PopulatedPublicationDatabaseAddsChatWithoutChangingExistingRowsOrArtifactBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "publish-chat.db")}")
                .Options;
            var projectId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var renderId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var artifactBytes = "immutable-pdf-fixture"u8.ToArray();
            const string artifactHash = "2e17f0f6950a45a27f5ff1671661567e2824f9f0ada753783fc046c632310e9d";
            var manuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapterId));

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Existing book",
                    Slug = $"existing-{projectId:N}",
                });
                db.PublicationEditions.Add(new PublicationEdition
                {
                    Id = editionId,
                    ProjectId = projectId,
                    Name = "Paperback",
                    IsDefault = true,
                    Revision = 7,
                });
                db.Chapters.Add(new Chapter
                {
                    Id = chapterId,
                    ProjectId = projectId,
                    Title = "Existing chapter",
                    ManuscriptJson = manuscriptJson,
                    ManuscriptRevision = 4,
                });
                db.PublicationRenderJobs.Add(new PublicationRenderJob
                {
                    Id = renderId,
                    EditionId = editionId,
                    Status = PublicationRenderStatus.Completed,
                    SourceFingerprint = "source-before-chat",
                    RendererVersion = "fixture",
                    ProfileId = "preview",
                    ProgressPercent = 100,
                    ProgressMessage = "Completed",
                });
                db.PublicationArtifacts.Add(new PublicationArtifact
                {
                    Id = artifactId,
                    EditionId = editionId,
                    RenderJobId = renderId,
                    Kind = PublicationArtifactKind.InteriorPdf,
                    FileName = "interior.pdf",
                    MediaType = "application/pdf",
                    Data = artifactBytes,
                    Sha256 = artifactHash,
                    ByteLength = artifactBytes.Length,
                    PageCount = 12,
                    SourceFingerprint = "source-before-chat",
                    RendererVersion = "fixture",
                    ProfileId = "preview",
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var edition = await db.PublicationEditions.AsNoTracking().SingleAsync();
                var chapter = await db.Chapters.AsNoTracking().SingleAsync();
                var render = await db.PublicationRenderJobs.AsNoTracking().SingleAsync();
                var artifact = await db.PublicationArtifacts.AsNoTracking().SingleAsync();
                Assert.Equal(editionId, edition.Id);
                Assert.Equal(7, edition.Revision);
                Assert.Equal(chapterId, chapter.Id);
                Assert.Equal(4, chapter.ManuscriptRevision);
                Assert.Equal(manuscriptJson, chapter.ManuscriptJson);
                Assert.Equal(renderId, render.Id);
                Assert.Equal("source-before-chat", render.SourceFingerprint);
                Assert.Equal(artifactId, artifact.Id);
                Assert.Equal(artifactHash, artifact.Sha256);
                Assert.Equal(artifactBytes, artifact.Data);

                var conversation = new PublishConversation { ProjectId = projectId };
                conversation.Messages.Add(new PublishMessage
                {
                    Order = 0,
                    Role = PublishMessageRole.Assistant,
                    Content = "Ready to publish.",
                });
                db.PublishConversations.Add(conversation);
                await db.SaveChangesAsync();

                var messages = await db.PublishMessages.AsNoTracking()
                    .Where(message => message.ConversationId == conversation.Id)
                    .OrderBy(message => message.Order)
                    .ToListAsync();
                Assert.Single(messages);
                Assert.Equal("Ready to publish.", messages[0].Content);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

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
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO Chapters (
                         Id, ProjectId, ActId, Title, Synopsis, "Order", VisualMode,
                         IllustrationLayoutJson, PageLayoutJson, PageLayoutKind,
                         ManuscriptJson, ManuscriptRevision, VectorIndexState, VectorIndexError,
                         VectorIndexedAt, CreatedAt, UpdatedAt)
                     VALUES ({chapterId}, {projectId}, NULL, 'Existing chapter', '', 0, 'Prose',
                         '', '', 'SinglePortrait', {manuscriptJson}, 4, 'Stale', NULL, NULL,
                         {DateTime.UtcNow}, {DateTime.UtcNow});
                     """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO PublicationEditions (
                         Id, ProjectId, Name, Format, Vendor, VendorProfileVersion, Status,
                         IsDefault, Revision, TitleOverride, Subtitle, Author, Language,
                         Publisher, Copyright, Isbn, Description, IncludeTableOfContents,
                         IncludeVisibleTableOfContents, IncludeActSynopses, IncludeChapterSynopses,
                         IncludeActHeadings, IncludeChapterHeadings, NumberActs, NumberChapters,
                         TitlePageMode, PrintPicturePageSpreadMode, EpubPicturePageSpreadMode,
                         Binding, Paper, Ink, Bleed, PageWidthInches, PageHeightInches,
                         PageMarginInches, BodyFontSizePoints, BodyLineHeight,
                         SelectedCoverChapterId, CreatedAt, UpdatedAt)
                     VALUES (
                         {editionId}, {projectId}, 'Paperback', 'Paperback', 'Generic', 'preview-1',
                         'Draft', 1, 7, '', '', '', 'en', '', '', '', '', 1, 1, 0, 0, 1, 1,
                         0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape', 'PerfectBound',
                         'White', 'BlackAndWhite', 0, 6, 9, 0.75, 11, 1.3,
                         NULL, {DateTime.UtcNow}, {DateTime.UtcNow});
                     """);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO PublicationRenderJobs (
                         Id, EditionId, Status, SourceFingerprint, RendererVersion, ProfileId,
                         DiagnosticsJson, EvidenceJson, ProgressPercent, ProgressMessage,
                         CancellationRequested, CreatedAt, StartedAt, CompletedAt)
                     VALUES ({renderId}, {editionId}, 'Completed', 'source-before-chat',
                         'fixture', 'preview', '[]', '', 100, 'Completed', 0,
                         {DateTime.UtcNow}, NULL, {DateTime.UtcNow});

                     INSERT INTO PublicationArtifacts (
                         Id, EditionId, RenderJobId, Kind, FileName, MediaType, Data, Sha256,
                         ByteLength, PageCount, SourceFingerprint, RendererVersion, ProfileId, CreatedAt)
                     VALUES ({artifactId}, {editionId}, {renderId}, 'InteriorPdf', 'interior.pdf',
                         'application/pdf', {artifactBytes}, {artifactHash}, {artifactBytes.Length},
                         12, 'source-before-chat', 'fixture', 'preview', {DateTime.UtcNow});
                     """);
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

                var toolMessage = new PublishMessage
                {
                    ConversationId = conversation.Id,
                    Order = 1,
                    Role = PublishMessageRole.Tool,
                    Content = "{\"ok\":true}",
                    ToolCallId = "preview-call",
                    ToolName = "preview_publication_cover_canvas",
                };
                toolMessage.Visuals.Add(new PublishMessageVisual
                {
                    SortOrder = 0,
                    ToolCallId = toolMessage.ToolCallId,
                    Title = "Cover preview",
                    Caption = "Migration-safe preview",
                    SourceKind = "publicationCoverCanvasPreview",
                    SourceRefId = projectId,
                    ContentType = "image/png",
                    FileName = "cover-preview.png",
                    Width = 12,
                    Height = 18,
                    Data = "preview-bytes"u8.ToArray(),
                });
                db.PublishMessages.Add(toolMessage);
                await db.SaveChangesAsync();

                var messages = await db.PublishMessages.AsNoTracking()
                    .Where(message => message.ConversationId == conversation.Id)
                    .OrderBy(message => message.Order)
                    .ToListAsync();
                Assert.Equal(2, messages.Count);
                Assert.Equal("Ready to publish.", messages[0].Content);
                var visual = await db.PublishMessageVisuals.AsNoTracking().SingleAsync();
                Assert.Equal(toolMessage.Id, visual.MessageId);
                Assert.Equal("preview-call", visual.ToolCallId);
                Assert.Equal("preview-bytes"u8.ToArray(), visual.Data);
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

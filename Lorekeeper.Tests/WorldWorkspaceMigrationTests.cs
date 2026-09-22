using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class WorldWorkspaceMigrationTests
{
    [Fact]
    public async Task Upgrade_preserves_briefs_and_chat_attachment_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectId = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "world.db")};Pooling=False").Options;
        try
        {
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260922160908_AddVoiceWorkspace");
                db.Projects.Add(new Project { Id = projectId, Name = "Voice fixture", Slug = "voice-fixture" });
                db.PublishAssets.Add(new PublishAsset { Id = imageId, ProjectId = projectId, FileName = "reference.png", ContentType = "image/png", Data = [1, 2, 3] });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ChatMessageImageAttachments (Id, ProjectId, Surface, MessageId, ImageId, SortOrder, CreatedAt) VALUES ({attachmentId}, {projectId}, 'Research', {messageId}, {imageId}, 0, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WritingSamples (Id, ProjectId, Title, Body, CreatedAt, UpdatedAt, Revision) VALUES ({sampleId}, {projectId}, 'Style', 'Exact sample prose', {now}, {now}, 0)");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ResearchConversations (Id, ProjectId, CreatedAt, UpdatedAt, SelectedProviderId) VALUES ({conversationId}, {projectId}, {now}, {now}, 123)");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ResearchMessages (Id, ConversationId, \"Order\", Role, Content, ToolCallsJson, Reasoning, Status, CreatedAt) VALUES ({messageId}, {conversationId}, 0, 'Assistant', 'Preserved voice discussion', '[]', '', 'Completed', {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO WebIngestCandidates
                    (Id, ProjectId, ResearchConversationId, DiscoveryKind, Status, Url, FinalUrl, CanonicalUrl, DisplayUrl, ParentUrl,
                     Title, Snippet, Excerpt, ExtractedText, CachedLinksJson, CachedImagesJson, ContentHash, ContentType,
                     SourceProviderName, SearchQuery, CrawlDepth, StageRationale, Diagnostics, RawMetadataJson, CreatedAt, UpdatedAt)
                    VALUES ({Guid.NewGuid()}, {projectId}, {conversationId}, 'SearchResult', 'Read', 'https://example.com/source',
                     '', '', '', '', 'Preserved source', '', '', 'Exact source text', '[]', '[]', 'source-hash', 'text/html',
                     'Provider', 'Original query', 0, '', '', '[]', {now}, {now})
                    """);
                db.GraphNodes.Add(new GraphNode { ProjectId = projectId, NodeType = "Character", Key = Guid.NewGuid().ToString("N"), Label = "Mira", Properties = new() { ["voiceProfile"] = "Sparse dialogue. Lyrical internal narration." } });
                await db.SaveChangesAsync();
            }
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var source = await db.WebIngestCandidates.SingleAsync();
                Assert.Equal(conversationId, source.WorldConversationId);
                Assert.Equal("Exact source text", source.ExtractedText);
                Assert.Equal("Original query", source.SearchQuery);
                var world = await db.WorldBriefs.SingleAsync();
                Assert.Equal(projectId, world.ProjectId);
                Assert.Empty(world.Content);
                Assert.Equal(0, world.Revision);
                var sample = await db.WritingSamples.SingleAsync();
                Assert.Equal(sampleId, sample.Id);
                Assert.Equal("Exact sample prose", sample.Body);
                Assert.Equal(0, sample.Revision);
                var conversation = await db.WorldConversations.SingleAsync();
                Assert.Equal(conversationId, conversation.Id);
                Assert.Equal(123, conversation.SelectedProviderId);
                Assert.Equal(messageId, (await db.WorldMessages.SingleAsync()).Id);
                var attachment = await db.ChatMessageImageAttachments.SingleAsync();
                Assert.Equal(attachmentId, attachment.Id);
                Assert.Equal(Lorekeeper.ChatTurns.ChatTurnSurface.World, attachment.Surface);
                Assert.Equal(messageId, attachment.MessageId);
                Assert.Equal(imageId, attachment.ImageId);
                Assert.Equal("Sparse dialogue. Lyrical internal narration.", (await db.GraphNodes.SingleAsync()).Properties["voiceProfile"]?.ToString());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

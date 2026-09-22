using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class VoiceWorkspaceMigrationTests
{
    [Fact]
    public async Task Upgrade_preserves_samples_profiles_and_chat_attachment_identity()
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
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "voice.db")};Pooling=False").Options;
        try
        {
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20260921032502_PreserveChatResponseProtocol");
                db.Projects.Add(new Project { Id = projectId, Name = "Voice fixture", Slug = "voice-fixture" });
                db.PublishAssets.Add(new PublishAsset { Id = imageId, ProjectId = projectId, FileName = "reference.png", ContentType = "image/png", Data = [1, 2, 3] });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ChatMessageImageAttachments (Id, ProjectId, Surface, MessageId, ImageId, SortOrder, CreatedAt) VALUES ({attachmentId}, {projectId}, 'WritingCoach', {messageId}, {imageId}, 0, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WritingSamples (Id, ProjectId, Title, Body, CreatedAt, UpdatedAt) VALUES ({sampleId}, {projectId}, 'Style', 'Exact sample prose', {now}, {now})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WritingCoachConversations (Id, ProjectId, CreatedAt, UpdatedAt, SelectedProviderId) VALUES ({conversationId}, {projectId}, {now}, {now}, 123)");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WritingCoachMessages (Id, ConversationId, \"Order\", Role, Content, ToolCallsJson, Reasoning, Status, CreatedAt) VALUES ({messageId}, {conversationId}, 0, 'Assistant', 'Preserved voice discussion', '[]', '', 'Completed', {now})");
                db.GraphNodes.Add(new GraphNode { ProjectId = projectId, NodeType = "Character", Key = Guid.NewGuid().ToString("N"), Label = "Mira", Properties = new() { ["voiceProfile"] = "Sparse dialogue. Lyrical internal narration." } });
                await db.SaveChangesAsync();
            }
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var sample = await db.WritingSamples.SingleAsync();
                Assert.Equal(sampleId, sample.Id);
                Assert.Equal("Exact sample prose", sample.Body);
                Assert.Equal(0, sample.Revision);
                var conversation = await db.VoiceConversations.SingleAsync();
                Assert.Equal(conversationId, conversation.Id);
                Assert.Equal(123, conversation.SelectedProviderId);
                Assert.Equal(messageId, (await db.VoiceMessages.SingleAsync()).Id);
                var attachment = await db.ChatMessageImageAttachments.SingleAsync();
                Assert.Equal(attachmentId, attachment.Id);
                Assert.Equal(Lorekeeper.ChatTurns.ChatTurnSurface.Voice, attachment.Surface);
                Assert.Equal(messageId, attachment.MessageId);
                Assert.Equal(imageId, attachment.ImageId);
                Assert.Equal("Sparse dialogue. Lyrical internal narration.", (await db.GraphNodes.SingleAsync()).Properties["voiceProfile"]?.ToString());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

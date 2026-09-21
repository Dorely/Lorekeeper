using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ChatResponseProtocolMigrationTests
{
    private const string PreviousMigration = "20260918230541_SourceJobProcessingModes";

    [Fact]
    public async Task PopulatedTranscriptsSurviveProtocolMigrationAndOpaqueMetadataRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "chat-response-protocol.db")};Pooling=False")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var projectId = Guid.NewGuid();
            var fixtures = new[]
            {
                new ConversationFixture("EditorConversations", "EditorMessages", "Editor"),
                new ConversationFixture("OutlineConversations", "OutlineMessages", "Outline"),
                new ConversationFixture("WritingCoachConversations", "WritingCoachMessages", "Writing Coach"),
                new ConversationFixture("ResearchConversations", "ResearchMessages", "Research"),
                new ConversationFixture("ProjectImageConversations", "ProjectImageMessages", "Images"),
                new ConversationFixture("PublishConversations", "PublishMessages", "Publish"),
            };

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Chat migration fixture",
                    Slug = $"chat-migration-{projectId:N}",
                });
                await db.SaveChangesAsync();

                foreach (var fixture in fixtures)
                {
                    var conversationId = Guid.NewGuid();
                    await InsertConversationAsync(db, fixture, projectId, conversationId);
                    await InsertMessageAsync(db, fixture, conversationId);
                }
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                Assert.Null((await db.EditorConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Null((await db.OutlineConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Null((await db.WritingCoachConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Null((await db.ResearchConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Null((await db.ProjectImageConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Null((await db.PublishConversations.AsNoTracking().SingleAsync()).SelectedProviderId);

                Assert.Equal("Editor transcript", (await db.EditorMessages.AsNoTracking().SingleAsync()).Content);
                Assert.Equal("Outline transcript", (await db.OutlineMessages.AsNoTracking().SingleAsync()).Content);
                Assert.Equal("Writing Coach transcript", (await db.WritingCoachMessages.AsNoTracking().SingleAsync()).Content);
                Assert.Equal("Research transcript", (await db.ResearchMessages.AsNoTracking().SingleAsync()).Content);
                Assert.Equal("Images transcript", (await db.ProjectImageMessages.AsNoTracking().SingleAsync()).Content);
                Assert.Equal("Publish transcript", (await db.PublishMessages.AsNoTracking().SingleAsync()).Content);
                Assert.Null((await db.EditorMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Null((await db.OutlineMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Null((await db.WritingCoachMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Null((await db.ResearchMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Null((await db.ProjectImageMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Null((await db.PublishMessages.SingleAsync()).ResponseMetadataJson);

                foreach (var fixture in fixtures)
                    await StoreProtocolAsync(db, fixture);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                Assert.Equal(ProtocolJson, (await db.EditorMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Equal(ProtocolJson, (await db.OutlineMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Equal(ProtocolJson, (await db.WritingCoachMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Equal(ProtocolJson, (await db.ResearchMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Equal(ProtocolJson, (await db.ProjectImageMessages.SingleAsync()).ResponseMetadataJson);
                Assert.Equal(ProtocolJson, (await db.PublishMessages.SingleAsync()).ResponseMetadataJson);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private const string ProtocolJson = """
        {"Version":1,"ConnectionId":17,"ModelId":"origin-model","EndpointFingerprint":"non-secret-fingerprint","ReasoningFields":{"reasoning_content":"First\\nsecond","reasoning_details":[{"type":"reasoning.encrypted","data":"AA+/==","signature":"opaque+/=","index":0,"vendor":{"unknown":[1,null,true]}}]},"FinishReason":"length","Usage":{"InputTokenCount":11,"OutputTokenCount":23,"ReasoningTokenCount":19}}
        """;

#pragma warning disable EF1002 // Fixture table names are fixed constants, while values remain parameters.
    private static Task StoreProtocolAsync(AppDbContext db, ConversationFixture fixture) =>
        db.Database.ExecuteSqlRawAsync(
            $"UPDATE {fixture.MessageTable} SET ResponseMetadataJson = $metadata",
            new SqliteParameter("$metadata", ProtocolJson));

    private static Task InsertConversationAsync(
        AppDbContext db,
        ConversationFixture fixture,
        Guid projectId,
        Guid conversationId) =>
        db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {fixture.ConversationTable} (Id, ProjectId, CreatedAt, UpdatedAt) VALUES ($id, $projectId, $createdAt, $updatedAt)",
            new SqliteParameter("$id", conversationId),
            new SqliteParameter("$projectId", projectId),
            new SqliteParameter("$createdAt", DateTime.UtcNow),
            new SqliteParameter("$updatedAt", DateTime.UtcNow));

    private static Task InsertMessageAsync(
        AppDbContext db,
        ConversationFixture fixture,
        Guid conversationId)
    {
        var content = $"{fixture.Surface} transcript";
        if (fixture.MessageTable == "EditorMessages")
        {
            return db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {fixture.MessageTable} (Id, ConversationId, \"Order\", Role, Content, ToolCallsJson, ContextSnapshotJson, ContentTargetKind, Status, CreatedAt) VALUES ($id, $conversationId, 0, 'Assistant', $content, '[]', NULL, 'Core', 'Completed', $createdAt)",
                new SqliteParameter("$id", Guid.NewGuid()),
                new SqliteParameter("$conversationId", conversationId),
                new SqliteParameter("$content", content),
                new SqliteParameter("$createdAt", DateTime.UtcNow));
        }

        return db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {fixture.MessageTable} (Id, ConversationId, \"Order\", Role, Content, ToolCallsJson, Status, CreatedAt) VALUES ($id, $conversationId, 0, 'Assistant', $content, '[]', 'Completed', $createdAt)",
            new SqliteParameter("$id", Guid.NewGuid()),
            new SqliteParameter("$conversationId", conversationId),
            new SqliteParameter("$content", content),
            new SqliteParameter("$createdAt", DateTime.UtcNow));
    }

    private sealed record ConversationFixture(string ConversationTable, string MessageTable, string Surface);
#pragma warning restore EF1002
}

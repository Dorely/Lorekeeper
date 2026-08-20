using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ChatConversationModelSelectionMigrationTests
{
    private const string PreviousMigration = "20260820050919_AddManuscriptAnnotations";

    [Fact]
    public async Task PopulatedChatConversationsAndMessagesSurviveModelSelectionMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "chat-model-selection.db")}")
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
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

#pragma warning disable EF1002 // Fixture table names are fixed constants, while values remain parameters.
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

using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class OpenAiAccountOwnershipMigrationTests
{
    private const string PreviousMigration = "20260908120000_AddProjectImageJobBackground";

    [Fact]
    public async Task LegacyCodexAccountMovesCredentialsWithoutChangingModelIdentitiesOrSelections()
    {
        var directory = CreateDirectory();
        try
        {
            var options = CreateOptions(directory, "account-migration.db");
            var projectId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO Projects
                        (Id, ContestModeEnabled, CreatedAt, IncludeCurrentChapterInContext, Name,
                         ProjectGuidance, ReviewEditsEnabled, Slug, UpdatedAt)
                    VALUES
                        ($id, 0, $createdAt, 1, 'Account migration', '', 1, $slug, $updatedAt)
                    """,
                    new SqliteParameter("$id", projectId),
                    new SqliteParameter("$createdAt", DateTime.UtcNow),
                    new SqliteParameter("$slug", $"account-{projectId:N}"),
                    new SqliteParameter("$updatedAt", DateTime.UtcNow));
                await InsertProviderAsync(db, 101, "openai-codex", "OpenAI (Codex)", "gpt-5.6-sol", null, true, "Maximum", 12_345);
                await InsertProviderAsync(db, 102, "openai-codex-gpt-5.6-sol-a", "Sol A", "gpt-5.6-sol", 101, false, "High", 23_456);
                await InsertProviderAsync(db, 103, "openai-codex-gpt-5.6-sol-b", "Sol B", "gpt-5.6-sol", 101, false, "Low", 34_567);
                await InsertProviderAsync(db, 104, "ollama-local", "Local", "llama3", null, false, null, 45_678, "None", "http://localhost:11434/v1");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO OAuthTokens (Id, ProviderId, AccessToken, RefreshToken, ExpiresAt, Scope, CreatedAt) VALUES (201, 101, 'access-secret', 'refresh-secret', $expiresAt, 'openid', $createdAt)",
                    new SqliteParameter("$expiresAt", DateTime.UtcNow.AddHours(1)),
                    new SqliteParameter("$createdAt", DateTime.UtcNow));
                await InsertConversationsAsync(db, projectId, 102);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await db.Database.MigrateAsync();

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var account = await db.OpenAiAccounts.AsNoTracking().SingleAsync();
                Assert.Equal(101, account.Id);
                Assert.Equal("OpenAI (Codex)", account.DisplayName);

                var accountModels = await db.LlmProviders.AsNoTracking()
                    .Where(provider => provider.OpenAiAccountId == account.Id)
                    .OrderBy(provider => provider.Id)
                    .ToListAsync();
                Assert.Equal([101, 102, 103], accountModels.Select(provider => provider.Id));
                Assert.All(accountModels, provider =>
                {
                    Assert.Null(provider.CredentialSourceId);
                    Assert.Equal(LlmModelOrigin.Manual, provider.ModelOrigin);
                    Assert.Equal(AccountModelAvailability.Unknown, provider.AccountAvailability);
                    Assert.Null(provider.DiscoveredContextWindowTokens);
                });
                Assert.Equal(2, accountModels.Count(provider => provider.ModelId == "gpt-5.6-sol" && provider.Id != 101));
                Assert.Equal(LlmReasoningEffort.High, accountModels.Single(provider => provider.Id == 102).ReasoningEffort);
                Assert.Equal(23_456, accountModels.Single(provider => provider.Id == 102).MaxInputTokens);
                Assert.True(accountModels.Single(provider => provider.Id == 101).IsDefault);

                var manual = await db.LlmProviders.AsNoTracking().SingleAsync(provider => provider.Id == 104);
                Assert.Null(manual.OpenAiAccountId);
                Assert.Equal("llama3", manual.ModelId);
                Assert.Equal(45_678, manual.MaxInputTokens);

                var token = await db.OAuthTokens.AsNoTracking().SingleAsync();
                Assert.Equal(201, token.Id);
                Assert.Equal(101, token.OpenAiAccountId);
                Assert.Equal("access-secret", token.AccessToken);
                Assert.Equal("refresh-secret", token.RefreshToken);

                Assert.Equal(102, (await db.EditorConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Equal(102, (await db.OutlineConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Equal(102, (await db.WritingCoachConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Equal(102, (await db.ResearchConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Equal(102, (await db.ProjectImageConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
                Assert.Equal(102, (await db.PublishConversations.AsNoTracking().SingleAsync()).SelectedProviderId);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task AmbiguousOAuthOwnershipFailsWithoutApplyingTheMigration()
    {
        var directory = CreateDirectory();
        try
        {
            var options = CreateOptions(directory, "ambiguous-account.db");
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                await InsertProviderAsync(db, 301, "unexpected-oauth", "Unexpected", "model", null, false, null, 1_000);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.MigrateAsync());

            await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "ambiguous-account.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE '%OpenAiAccountOwnership';";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('OAuthTokens') WHERE name = 'ProviderId';";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static DbContextOptions<AppDbContext> CreateOptions(string directory, string fileName) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, fileName)}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

    private static Task InsertProviderAsync(
        AppDbContext db,
        int id,
        string name,
        string displayName,
        string modelId,
        int? credentialSourceId,
        bool isDefault,
        string? reasoningEffort,
        int maxInputTokens,
        string authType = "OAuth",
        string endpoint = "https://chatgpt.com/backend-api/codex/responses") =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO LlmProviders
                (Id, Name, DisplayName, EndpointUrl, ModelId, ReasoningEffort, MaxInputTokens,
                 MaxTokensField, AuthType, ApiKey, IsDefault, LastChatTestSucceeded,
                 LastVisionTestSucceeded, CredentialSourceId, CreatedAt, UpdatedAt)
            VALUES
                ($id, $name, $displayName, $endpoint, $modelId, $reasoningEffort, $maxInputTokens,
                 'Default', $authType, NULL, $isDefault, 1, 1, $credentialSourceId, $createdAt, $updatedAt)
            """,
            new SqliteParameter("$id", id),
            new SqliteParameter("$name", name),
            new SqliteParameter("$displayName", displayName),
            new SqliteParameter("$endpoint", endpoint),
            new SqliteParameter("$modelId", modelId),
            new SqliteParameter("$reasoningEffort", (object?)reasoningEffort ?? DBNull.Value),
            new SqliteParameter("$maxInputTokens", maxInputTokens),
            new SqliteParameter("$authType", authType),
            new SqliteParameter("$isDefault", isDefault),
            new SqliteParameter("$credentialSourceId", (object?)credentialSourceId ?? DBNull.Value),
            new SqliteParameter("$createdAt", DateTime.UtcNow),
            new SqliteParameter("$updatedAt", DateTime.UtcNow));

    private static async Task InsertConversationsAsync(AppDbContext db, Guid projectId, int providerId)
    {
        foreach (var table in new[]
                 {
                     "EditorConversations", "OutlineConversations", "WritingCoachConversations",
                     "ResearchConversations", "ProjectImageConversations", "PublishConversations",
                 })
        {
#pragma warning disable EF1002 // Table names are fixed test fixtures.
            await db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {table} (Id, ProjectId, SelectedProviderId, CreatedAt, UpdatedAt) VALUES ($id, $projectId, $providerId, $createdAt, $updatedAt)",
                new SqliteParameter("$id", Guid.NewGuid()),
                new SqliteParameter("$projectId", projectId),
                new SqliteParameter("$providerId", providerId),
                new SqliteParameter("$createdAt", DateTime.UtcNow),
                new SqliteParameter("$updatedAt", DateTime.UtcNow));
#pragma warning restore EF1002
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}

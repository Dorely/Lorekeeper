using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ChatGptPlanAccountMigrationTests
{
    private const string PreviousMigration = "20261020235109_AccountModelAvailability";

    [Fact]
    public async Task Downgrade_removes_plan_account_and_upgrade_keeps_codex_account_as_codex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "chatgpt-plan-accounts.db")};Pooling=False")
            .Options;
        try
        {
            int codexAccountId;
            int codexModelId;
            int codexTokenId;
            int localId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var codex = new OpenAiAccount
                {
                    Kind = OpenAiAccountKind.Codex,
                    DisplayName = "Codex fixture",
                    ExternalAccountId = "codex-account-fixture",
                };
                var plan = new OpenAiAccount
                {
                    Kind = OpenAiAccountKind.ChatGptPlan,
                    DisplayName = "Plan fixture",
                    ExternalAccountId = "plan-subject-fixture",
                    IssuedClientId = "oaiapp_fixture",
                    HostId = "urn:uuid:00000000-0000-0000-0000-000000000001",
                    PlanUsageGranted = true,
                };
                db.OpenAiAccounts.AddRange(codex, plan);
                var codexModel = new LlmProvider
                {
                    Name = "openai-account-1-gpt-6-luna",
                    EndpointUrl = "https://chatgpt.com/backend-api/codex/responses",
                    ModelId = "gpt-6-luna",
                    AuthType = AuthType.OAuth,
                    OpenAiAccount = codex,
                    ModelOrigin = LlmModelOrigin.BundledCatalog,
                    IsDefault = true,
                };
                var planModel = new LlmProvider
                {
                    Name = "openai-account-2-gpt-6-luna",
                    EndpointUrl = "https://api.openai.com/v1/responses",
                    ModelId = "gpt-6-luna",
                    AuthType = AuthType.OAuth,
                    OpenAiAccount = plan,
                    ModelOrigin = LlmModelOrigin.BundledCatalog,
                };
                var local = new LlmProvider
                {
                    Name = "ollama",
                    EndpointUrl = "http://localhost:11434/v1",
                    ModelId = "qwen3",
                    AuthType = AuthType.None,
                };
                db.LlmProviders.AddRange(codexModel, planModel, local);
                var codexToken = new OAuthToken
                {
                    OpenAiAccount = codex,
                    AccessToken = "codex-access-fixture",
                    RefreshToken = "codex-refresh-fixture",
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                };
                db.OAuthTokens.AddRange(
                    codexToken,
                    new OAuthToken
                    {
                        OpenAiAccount = plan,
                        AccessToken = "plan-access-fixture",
                        RefreshToken = "plan-refresh-fixture",
                        IdToken = "plan-id-token-fixture",
                        ExpiresAt = DateTime.UtcNow.AddHours(1),
                    });
                await db.SaveChangesAsync();
                codexAccountId = codex.Id;
                codexModelId = codexModel.Id;
                codexTokenId = codexToken.Id;
                localId = local.Id;
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();

                var account = await db.OpenAiAccounts.AsNoTracking().SingleAsync();
                Assert.Equal(codexAccountId, account.Id);
                Assert.Equal(OpenAiAccountKind.Codex, account.Kind);
                Assert.Equal("codex-account-fixture", account.ExternalAccountId);
                Assert.Null(account.IssuedClientId);
                Assert.Null(account.HostId);
                Assert.False(account.PlanUsageGranted);

                var providers = await db.LlmProviders.AsNoTracking().ToListAsync();
                Assert.Equal(
                    new[] { codexModelId, localId }.Order(),
                    providers.Select(provider => provider.Id).Order());
                Assert.True(providers.Single(provider => provider.Id == codexModelId).IsDefault);

                var token = await db.OAuthTokens.AsNoTracking().SingleAsync();
                Assert.Equal(codexTokenId, token.Id);
                Assert.Equal(codexAccountId, token.OpenAiAccountId);
                Assert.Equal("codex-access-fixture", token.AccessToken);
                Assert.Equal("codex-refresh-fixture", token.RefreshToken);
                Assert.Null(token.IdToken);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

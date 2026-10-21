using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class AccountModelAvailabilityMigrationTests
{
    private const string PreviousMigration = "20261017013946_PreservePublicationBlockIdentifiers";

    [Fact]
    public async Task Downgrade_and_upgrade_keep_account_models_and_reset_availability()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "account-model-availability.db")};Pooling=False")
            .Options;
        try
        {
            int accountId;
            int catalogId;
            int manualAccountId;
            int localId;
            int discoveredId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var account = new OpenAiAccount { DisplayName = "Account fixture", ExternalAccountId = "account-fixture" };
                db.OpenAiAccounts.Add(account);
                var catalog = new LlmProvider
                {
                    Name = "openai-account-1-gpt-6.1-sol",
                    DisplayName = "6.1 Sol",
                    EndpointUrl = "https://chatgpt.com/backend-api/codex/responses",
                    ModelId = "gpt-6.1-sol",
                    AuthType = AuthType.OAuth,
                    OpenAiAccount = account,
                    ModelOrigin = LlmModelOrigin.BundledCatalog,
                    ReasoningEffort = LlmReasoningEffort.High,
                    IsDefault = true,
                };
                var manualAccount = new LlmProvider
                {
                    Name = "openai-account-manual",
                    EndpointUrl = "https://chatgpt.com/backend-api/codex/responses",
                    ModelId = "gpt-manual-fixture",
                    AuthType = AuthType.OAuth,
                    OpenAiAccount = account,
                    ModelOrigin = LlmModelOrigin.Manual,
                    MaxInputTokens = 12_345,
                };
                var local = new LlmProvider
                {
                    Name = "ollama",
                    EndpointUrl = "http://localhost:11434/v1",
                    ModelId = "qwen3",
                    AuthType = AuthType.None,
                };
                var discovered = new LlmProvider
                {
                    Name = "openai-account-1-gpt-discovered-fixture",
                    EndpointUrl = "https://chatgpt.com/backend-api/codex/responses",
                    ModelId = "gpt-discovered-fixture",
                    AuthType = AuthType.OAuth,
                    OpenAiAccount = account,
                    ModelOrigin = LlmModelOrigin.AccountDiscovered,
                    AccountAvailability = LlmAccountModelAvailability.Available,
                    AccountAvailabilityCheckedAt = DateTime.UtcNow,
                    AccountModelMetadataJson = "{}",
                };
                db.LlmProviders.AddRange(catalog, manualAccount, local, discovered);
                await db.SaveChangesAsync();
                accountId = account.Id;
                catalogId = catalog.Id;
                manualAccountId = manualAccount.Id;
                localId = local.Id;
                discoveredId = discovered.Id;
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();

                var account = await db.OpenAiAccounts.AsNoTracking().SingleAsync();
                Assert.Equal(accountId, account.Id);
                Assert.Equal("account-fixture", account.ExternalAccountId);
                Assert.Null(account.LastModelRefreshAt);
                Assert.Null(account.LastModelRefreshError);

                var providers = await db.LlmProviders.AsNoTracking().OrderBy(provider => provider.Id).ToListAsync();
                Assert.Equal(
                    new[] { catalogId, manualAccountId, localId, discoveredId }.Order(),
                    providers.Select(provider => provider.Id));
                Assert.All(providers, provider =>
                {
                    Assert.Equal(LlmAccountModelAvailability.Unknown, provider.AccountAvailability);
                    Assert.Null(provider.AccountAvailabilityCheckedAt);
                    Assert.Null(provider.AccountModelMetadataJson);
                });

                var catalog = providers.Single(provider => provider.Id == catalogId);
                Assert.Equal(LlmModelOrigin.BundledCatalog, catalog.ModelOrigin);
                Assert.Equal(LlmReasoningEffort.High, catalog.ReasoningEffort);
                Assert.Equal(accountId, catalog.OpenAiAccountId);
                Assert.True(catalog.IsDefault);

                var manualAccount = providers.Single(provider => provider.Id == manualAccountId);
                Assert.Equal(LlmModelOrigin.Manual, manualAccount.ModelOrigin);
                Assert.Equal(12_345, manualAccount.MaxInputTokens);

                Assert.Null(providers.Single(provider => provider.Id == localId).OpenAiAccountId);

                var downgradedDiscovered = providers.Single(provider => provider.Id == discoveredId);
                Assert.Equal(LlmModelOrigin.Manual, downgradedDiscovered.ModelOrigin);
                Assert.Equal(accountId, downgradedDiscovered.OpenAiAccountId);
                Assert.Equal("gpt-discovered-fixture", downgradedDiscovered.ModelId);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

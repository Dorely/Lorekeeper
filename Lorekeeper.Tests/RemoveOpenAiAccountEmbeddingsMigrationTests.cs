using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class RemoveOpenAiAccountEmbeddingsMigrationTests
{
    private const string PreviousMigration = "20261007065536_GenericPrinterDimensions";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upgrade_removes_only_an_embedding_configuration_on_an_openai_account_connection(bool accountBacked)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "account-embeddings.db")};Pooling=False")
            .Options;
        try
        {
            int providerId;
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var account = new OpenAiAccount { ExternalAccountId = "account-fixture" };
                var provider = accountBacked
                    ? new LlmProvider
                    {
                        Name = "openai-account",
                        EndpointUrl = "https://chatgpt.com/backend-api/codex/responses",
                        ModelId = "gpt-6-sol",
                        AuthType = AuthType.OAuth,
                        OpenAiAccount = account,
                    }
                    : new LlmProvider
                    {
                        Name = "ollama",
                        EndpointUrl = "http://localhost:11434/v1",
                        ModelId = "qwen3",
                        AuthType = AuthType.None,
                    };
                db.OpenAiAccounts.Add(account);
                db.LlmProviders.Add(provider);
                await db.SaveChangesAsync();
                providerId = provider.Id;
                db.EmbeddingConfigurations.Add(new EmbeddingConfiguration
                {
                    ProviderId = providerId,
                    ApiKind = EmbeddingApiKind.OpenAICompatible,
                    ModelId = "embedding-fixture",
                    Dimensions = 768,
                    LastTestedProviderId = providerId,
                    LastTestedApiKind = EmbeddingApiKind.OpenAICompatible,
                    LastTestedModelId = "embedding-fixture",
                    LastTestedDimensions = 768,
                    LastTestedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                var configuration = await db.EmbeddingConfigurations.AsNoTracking().SingleOrDefaultAsync();
                if (accountBacked)
                    Assert.Null(configuration);
                else
                    Assert.Equal(providerId, Assert.IsType<EmbeddingConfiguration>(configuration).ProviderId);
                Assert.True(await db.LlmProviders.AnyAsync(provider => provider.Id == providerId));
                Assert.Equal(1, await db.OpenAiAccounts.CountAsync());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

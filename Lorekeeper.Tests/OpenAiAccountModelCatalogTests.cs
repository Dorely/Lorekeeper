using Lorekeeper.Authorization;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class OpenAiAccountModelCatalogTests
{
    [Fact]
    public void VersionOneCatalogContainsOnlyTheValidatedStableFour()
    {
        Assert.Equal(1, OpenAiAccountModelCatalog.SchemaVersion);
        Assert.Equal("https://learn.chatgpt.com/docs/models", OpenAiAccountModelCatalog.Source);
        Assert.Equal(new DateOnly(2026, 9, 16), OpenAiAccountModelCatalog.ValidationDate);
        Assert.Equal(
            ["gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"],
            OpenAiAccountModelCatalog.Entries.Select(entry => entry.ModelId));
        Assert.Equal("gpt-5.6-sol", OpenAiAccountModelCatalog.Preferred.ModelId);

        var expectedEfforts = new[]
        {
            LlmReasoningEffort.Low,
            LlmReasoningEffort.Medium,
            LlmReasoningEffort.High,
            LlmReasoningEffort.ExtraHigh,
            LlmReasoningEffort.Maximum,
        };
        Assert.All(OpenAiAccountModelCatalog.Entries, entry =>
        {
            Assert.Equal(expectedEfforts, entry.SupportedEfforts);
            Assert.Equal(
                LlmModelCapabilities.TextInput | LlmModelCapabilities.ImageInput,
                entry.Capabilities);
            Assert.Equal(272_000, entry.UsableInputBudgetTokens);
        });
        Assert.Equal(LlmReasoningEffort.Medium, OpenAiAccountModelCatalog.Find("gpt-6-astra")!.DefaultEffort);
        Assert.Equal(LlmReasoningEffort.Low, OpenAiAccountModelCatalog.Find("gpt-5.6-sol")!.DefaultEffort);
        Assert.Equal(LlmReasoningEffort.Medium, OpenAiAccountModelCatalog.Find("gpt-5.6-terra")!.DefaultEffort);
        Assert.Equal(LlmReasoningEffort.Medium, OpenAiAccountModelCatalog.Find("gpt-5.6-luna")!.DefaultEffort);
        Assert.DoesNotContain(OpenAiAccountModelCatalog.Entries, entry =>
            entry.ModelId.Contains("spark", StringComparison.OrdinalIgnoreCase)
            || entry.ModelId is "gpt-5.4" or "gpt-5.5");
    }

    [Fact]
    public async Task EnsureCatalogIsIdempotentAndPreservesManualRowsAndExistingDefault()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var account = await fixture.AddAccountAsync("Fresh");

        var first = await fixture.Catalog.EnsureCatalogAsync(account.Id);
        var second = await fixture.Catalog.EnsureCatalogAsync(account.Id);

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.Equal(4, second.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Count());
        var preferred = second.Single(model => model.ModelId == "gpt-5.6-sol");
        Assert.True(preferred.IsDefault);
        Assert.Null(preferred.ReasoningEffort);
        Assert.Null(preferred.MaxInputTokens);
        Assert.Equal(LlmReasoningEffort.Low, preferred.EffectiveReasoningEffort);
        Assert.Equal(272_000, preferred.EffectiveMaxInputTokens);
        Assert.Equal(account.Id, preferred.ResolvedMetadata!.OpenAiAccountId);
        Assert.Equal(1, preferred.ResolvedMetadata.CatalogSchemaVersion);
        Assert.Equal(OpenAiAccountModelCatalog.Source, preferred.ResolvedMetadata.CatalogSource);
        Assert.Equal(OpenAiAccountModelCatalog.ValidationDate, preferred.ResolvedMetadata.CatalogValidationDate);
        preferred.ReasoningEffort = LlmReasoningEffort.High;
        preferred.MaxInputTokens = 250_000;
        await fixture.ProviderService.UpdateAsync(preferred);
        Assert.Equal(LlmReasoningEffort.High, preferred.EffectiveReasoningEffort);
        Assert.Equal(250_000, preferred.EffectiveMaxInputTokens);
        preferred.IsDefault = false;
        await fixture.ProviderService.UpdateAsync(preferred);

        var existingDefault = await fixture.AddProviderAsync(new LlmProvider
        {
            Name = "local-default",
            DisplayName = "Local default",
            EndpointUrl = "http://localhost:11434/v1",
            ModelId = "llama3",
            AuthType = AuthType.None,
            IsDefault = true,
        });
        var secondAccount = await fixture.AddAccountAsync("Migrated");
        var manual = await fixture.AddProviderAsync(new LlmProvider
        {
            Name = "saved-manual-sol",
            DisplayName = "Saved Sol override",
            EndpointUrl = CodexProvider.ResponsesEndpoint,
            ModelId = "gpt-5.6-sol",
            ReasoningEffort = LlmReasoningEffort.High,
            MaxInputTokens = 123_456,
            AuthType = AuthType.OAuth,
            OpenAiAccountId = secondAccount.Id,
            ModelOrigin = LlmModelOrigin.Manual,
        });
        var retiringManual = await fixture.AddProviderAsync(new LlmProvider
        {
            Name = "saved-manual-gpt-5-5",
            DisplayName = "Saved GPT-5.5",
            EndpointUrl = CodexProvider.ResponsesEndpoint,
            ModelId = "gpt-5.5",
            ReasoningEffort = LlmReasoningEffort.Maximum,
            MaxInputTokens = 200_000,
            AuthType = AuthType.OAuth,
            OpenAiAccountId = secondAccount.Id,
            ModelOrigin = LlmModelOrigin.Manual,
        });

        var migrated = await fixture.Catalog.EnsureCatalogAsync(secondAccount.Id);

        Assert.Equal(6, migrated.Count);
        Assert.Equal(4, migrated.Count(model => model.ModelOrigin == LlmModelOrigin.BundledCatalog));
        var preserved = migrated.Single(model => model.Id == manual.Id);
        Assert.Equal(LlmModelOrigin.Manual, preserved.ModelOrigin);
        Assert.Equal(LlmReasoningEffort.High, preserved.ReasoningEffort);
        Assert.Equal(123_456, preserved.MaxInputTokens);
        Assert.Equal("gpt-5.5", migrated.Single(model => model.Id == retiringManual.Id).ModelId);
        Assert.Equal(LlmModelOrigin.Manual, migrated.Single(model => model.Id == retiringManual.Id).ModelOrigin);
        Assert.DoesNotContain(migrated, model => model.IsDefault);
        Assert.True((await fixture.ProviderService.GetByIdAsync(existingDefault.Id))!.IsDefault);
    }

    [Fact]
    public async Task ReconciliationKeepsCatalogAuthorityAndLastKnownMetadataOnFailure()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var account = await fixture.AddAccountAsync("Discovery");
        var manual = await fixture.AddProviderAsync(new LlmProvider
        {
            Name = "manual-account-model",
            DisplayName = "Manual account model",
            EndpointUrl = CodexProvider.ResponsesEndpoint,
            ModelId = "manual-model",
            ReasoningEffort = LlmReasoningEffort.None,
            MaxInputTokens = 90_000,
            AuthType = AuthType.OAuth,
            OpenAiAccountId = account.Id,
            ModelOrigin = LlmModelOrigin.Manual,
        });
        await fixture.Catalog.EnsureCatalogAsync(account.Id);
        var firstCheck = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc);

        await fixture.Catalog.ReconcileAvailabilityAsync(
            account.Id,
            [new("gpt-6-astra", 400_000), new("gpt-5.6-sol", 350_000), new("manual-model", 90_000)],
            firstCheck);
        var secondCheck = firstCheck.AddMinutes(5);
        var reconciled = await fixture.Catalog.ReconcileAvailabilityAsync(
            account.Id,
            [new("gpt-6-astra", 410_000), new("manual-model", null)],
            secondCheck);

        var astra = reconciled.Single(model => model.ModelId == "gpt-6-astra");
        Assert.Equal(AccountModelAvailability.Available, astra.AccountAvailability);
        Assert.Equal(410_000, astra.DiscoveredContextWindowTokens);
        Assert.Equal(272_000, astra.EffectiveMaxInputTokens);
        var sol = reconciled.Single(model =>
            model.ModelOrigin == LlmModelOrigin.BundledCatalog && model.ModelId == "gpt-5.6-sol");
        Assert.Equal(AccountModelAvailability.Unavailable, sol.AccountAvailability);
        Assert.Equal(350_000, sol.DiscoveredContextWindowTokens);
        var preservedManual = reconciled.Single(model => model.Id == manual.Id);
        Assert.Equal(LlmModelOrigin.Manual, preservedManual.ModelOrigin);
        Assert.Equal(LlmReasoningEffort.None, preservedManual.ReasoningEffort);
        Assert.Equal(90_000, preservedManual.MaxInputTokens);

        var failed = await fixture.Catalog.RecordRefreshFailureAsync(account.Id, secondCheck.AddMinutes(5));
        Assert.Equal(AccountModelAvailability.Available,
            failed.Single(model => model.Id == astra.Id).AccountAvailability);
        Assert.Equal(410_000, failed.Single(model => model.Id == astra.Id).DiscoveredContextWindowTokens);
        Assert.All(failed, model => Assert.Contains("retry", model.AccountAvailabilityError!, StringComparison.OrdinalIgnoreCase));
        var storedAccount = await fixture.Accounts.GetByIdAsync(account.Id);
        Assert.Equal(secondCheck.AddMinutes(5), storedAccount!.LastCatalogRefreshAt);
        Assert.Contains("last-known", storedAccount.LastCatalogRefreshError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BundledModelsNeedCredentialsAndEntitlementButNotManualTestsOrSilentFallback()
    {
        await using var fixture = await CatalogFixture.CreateAsync();
        var account = await fixture.AddAccountAsync("Readiness");
        fixture.Auth.ConnectedAccountId = account.Id;
        await fixture.Catalog.EnsureCatalogAsync(account.Id);
        await fixture.Catalog.ReconcileAvailabilityAsync(
            account.Id,
            [new("gpt-6-astra", null), new("gpt-5.6-sol", null)],
            DateTime.UtcNow);

        var models = await fixture.ProviderService.GetAllAsync();
        var sol = models.Single(model =>
            model.ModelOrigin == LlmModelOrigin.BundledCatalog && model.ModelId == "gpt-5.6-sol");
        var astra = models.Single(model => model.ModelId == "gpt-6-astra");
        Assert.False(sol.HasCurrentChatTestSnapshot);
        Assert.True(await fixture.ProviderService.IsChatProviderWorkingAsync(sol.Id));
        Assert.True(await fixture.ProviderService.IsVisionProviderWorkingAsync(sol.Id));
        Assert.True(await fixture.ProviderService.IsChatProviderWorkingAsync(astra.Id));

        await fixture.Catalog.ReconcileAvailabilityAsync(
            account.Id,
            [new("gpt-6-astra", null)],
            DateTime.UtcNow.AddMinutes(1));
        var defaultAvailability = await fixture.ProviderService.GetDefaultChatProviderAvailabilityAsync();
        Assert.False(defaultAvailability.IsAvailable);
        Assert.Equal(sol.Id, defaultAvailability.Provider!.Id);
        Assert.Contains("not available", defaultAvailability.Message, StringComparison.OrdinalIgnoreCase);

        var selection = await fixture.ProviderService.ResolveChatModelSelectionAsync(sol.Id);
        Assert.False(selection.IsAvailable);
        Assert.True(selection.IsUnavailableOverride);
        Assert.Equal(sol.Id, selection.ProviderId);

        var manual = await fixture.AddProviderAsync(new LlmProvider
        {
            Name = "untested-account-manual",
            DisplayName = "Untested manual",
            EndpointUrl = CodexProvider.ResponsesEndpoint,
            ModelId = "manual-model",
            AuthType = AuthType.OAuth,
            OpenAiAccountId = account.Id,
            ModelOrigin = LlmModelOrigin.Manual,
            AccountAvailability = AccountModelAvailability.Available,
        });
        Assert.False(await fixture.ProviderService.IsChatProviderWorkingAsync(manual.Id));

        astra.ReasoningEffort = LlmReasoningEffort.None;
        var validation = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.ProviderService.UpdateAsync(astra));
        Assert.Contains("does not support", validation.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CatalogFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IAppDatabaseOperationFactory _database;

        private CatalogFixture(SqliteConnection connection, IAppDatabaseOperationFactory database)
        {
            _connection = connection;
            _database = database;
            Auth = new FakeOpenAiAccountTokenService();
            Accounts = new OpenAiAccountService(database);
            ProviderService = new LlmProviderService(database, Auth);
            Catalog = new OpenAiAccountModelCatalogService(
                database,
                new FakeModelCatalogService(),
                NullLogger<OpenAiAccountModelCatalogService>.Instance);
        }

        public FakeOpenAiAccountTokenService Auth { get; }
        public IOpenAiAccountService Accounts { get; }
        public LlmProviderService ProviderService { get; }
        public OpenAiAccountModelCatalogService Catalog { get; }

        public static async Task<CatalogFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(connection);
            await using (var db = factory.CreateDbContext())
                await db.Database.MigrateAsync();
            var database = new AppDatabaseOperationFactory(
                factory,
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator());
            return new CatalogFixture(connection, database);
        }

        public Task<OpenAiAccount> AddAccountAsync(string displayName) =>
            Accounts.CreateAsync(displayName);

        public Task<LlmProvider> AddProviderAsync(LlmProvider provider) =>
            ProviderService.CreateAsync(provider);

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        public AppDbContext CreateDbContext() =>
            new(_options, NullLogger<AppDbContext>.Instance);
    }

    private sealed class FakeOpenAiAccountTokenService : IOpenAiAccountTokenService
    {
        public int? ConnectedAccountId { get; set; }

        public Task<OpenAiAccountAccess?> GetValidAccessAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(accountId == ConnectedAccountId
                ? new OpenAiAccountAccess(accountId, "external-test-account", "valid-test-token")
                : null);

        public Task DisconnectAsync(int accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeModelCatalogService : IModelCatalogService
    {
        public Task<IReadOnlyList<LlmDiscoveredModel>> ListChatModelsAsync(
            LlmProvider provider,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("External model discovery is not used by these deterministic tests.");

        public Task<IReadOnlyList<string>> ListEmbeddingModelsAsync(
            LlmProvider provider,
            EmbeddingApiKind apiKind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

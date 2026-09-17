using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Llm;

public sealed class OpenAiAccountModelCatalogService(
    IAppDatabaseOperationFactory database,
    IModelCatalogService modelCatalog,
    ILogger<OpenAiAccountModelCatalogService> logger) : IOpenAiAccountModelCatalogService
{
    private const string RefreshFailureMessage =
        "Model availability could not be refreshed. The bundled and last-known catalog is still available; retry when the connection is ready.";

    public async Task<IReadOnlyList<LlmProvider>> EnsureCatalogAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var accounts = operation.Repositories.OpenAiAccounts;
        var account = await accounts.GetByIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException("The OpenAI account was not found.");
        var providers = operation.Repositories.LlmProviders;
        var all = await providers.GetAllAsync(cancellationToken);
        var accountModels = all.Where(provider => provider.OpenAiAccountId == accountId).ToList();
        var freshAccount = accountModels.Count == 0;
        var hasGlobalDefault = all.Any(provider => provider.IsDefault);

        foreach (var obsoleteCatalogRow in accountModels.Where(provider =>
                     provider.ModelOrigin == LlmModelOrigin.BundledCatalog
                     && OpenAiAccountModelCatalog.Find(provider.ModelId) is null))
        {
            obsoleteCatalogRow.ModelOrigin = LlmModelOrigin.Manual;
            obsoleteCatalogRow.UpdatedAt = DateTime.UtcNow;
            providers.Update(obsoleteCatalogRow);
        }

        foreach (var entry in OpenAiAccountModelCatalog.Entries)
        {
            var matches = accountModels
                .Where(provider => provider.ModelOrigin == LlmModelOrigin.BundledCatalog
                    && string.Equals(provider.ModelId, entry.ModelId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException($"The OpenAI account contains duplicate catalog rows for {entry.ModelId}.");

            if (matches.SingleOrDefault() is { } existing)
            {
                existing.DisplayName = entry.DisplayName;
                existing.EndpointUrl = CodexProvider.ResponsesEndpoint;
                existing.AuthType = AuthType.OAuth;
                existing.ApiKey = null;
                existing.UpdatedAt = DateTime.UtcNow;
                providers.Update(existing);
                continue;
            }

            var providerName = OpenAiAccountModelCatalog.ProviderName(accountId, entry.ModelId);
            if (all.Any(provider => string.Equals(provider.Name, providerName, StringComparison.Ordinal)))
                providerName += $"-catalog-v{OpenAiAccountModelCatalog.SchemaVersion}";
            if (all.Any(provider => string.Equals(provider.Name, providerName, StringComparison.Ordinal)))
                throw new InvalidOperationException($"A saved provider name conflicts with the catalog row for {entry.ModelId}.");

            var model = new LlmProvider
            {
                Name = providerName,
                DisplayName = entry.DisplayName,
                EndpointUrl = CodexProvider.ResponsesEndpoint,
                ModelId = entry.ModelId,
                AuthType = AuthType.OAuth,
                OpenAiAccountId = accountId,
                ModelOrigin = LlmModelOrigin.BundledCatalog,
                AccountAvailability = AccountModelAvailability.Unknown,
                IsDefault = freshAccount && !hasGlobalDefault && entry.IsPreferred,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await providers.AddAsync(model, cancellationToken);
            all.Add(model);
            accountModels.Add(model);
        }

        account.UpdatedAt = DateTime.UtcNow;
        accounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
        return Resolve(accountModels);
    }

    public async Task<AccountModelCatalogRefreshResult> RefreshAvailabilityAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var models = await EnsureCatalogAsync(accountId, cancellationToken);
        var transport = models.FirstOrDefault()
            ?? throw new InvalidOperationException("The OpenAI account has no model catalog.");

        try
        {
            var discovered = await modelCatalog.ListChatModelsAsync(transport, cancellationToken);
            var reconciled = await ReconcileAvailabilityAsync(
                accountId,
                discovered,
                DateTime.UtcNow,
                cancellationToken);
            return new AccountModelCatalogRefreshResult(true, "Model availability refreshed.", reconciled);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "OpenAI account model refresh failed for account {AccountId}", accountId);
            var fallback = await RecordRefreshFailureAsync(accountId, DateTime.UtcNow, cancellationToken);
            return new AccountModelCatalogRefreshResult(false, RefreshFailureMessage, fallback);
        }
    }

    public async Task<IReadOnlyList<LlmProvider>> ReconcileAvailabilityAsync(
        int accountId,
        IReadOnlyList<LlmDiscoveredModel> discoveredModels,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureCatalogAsync(accountId, cancellationToken);
        var discovered = discoveredModels
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException("The OpenAI account was not found.");
        var all = await operation.Repositories.LlmProviders.GetAllAsync(cancellationToken);
        var accountModels = all.Where(provider => provider.OpenAiAccountId == accountId).ToList();

        foreach (var model in accountModels)
        {
            if (discovered.TryGetValue(model.ModelId, out var advertised))
            {
                model.AccountAvailability = AccountModelAvailability.Available;
                if (advertised.ContextLengthTokens is > 0)
                {
                    model.DiscoveredContextWindowTokens =
                        (int)Math.Min(advertised.ContextLengthTokens.Value, int.MaxValue);
                }
            }
            else
            {
                model.AccountAvailability = AccountModelAvailability.Unavailable;
            }

            model.AccountAvailabilityCheckedAt = checkedAtUtc;
            model.AccountAvailabilityError = null;
            model.UpdatedAt = checkedAtUtc;
            operation.Repositories.LlmProviders.Update(model);
        }

        account.LastCatalogRefreshAt = checkedAtUtc;
        account.LastCatalogRefreshError = null;
        account.UpdatedAt = checkedAtUtc;
        operation.Repositories.OpenAiAccounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
        return Resolve(accountModels);
    }

    public async Task<IReadOnlyList<LlmProvider>> RecordRefreshFailureAsync(
        int accountId,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await EnsureCatalogAsync(accountId, cancellationToken);
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var account = await operation.Repositories.OpenAiAccounts.GetByIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException("The OpenAI account was not found.");
        var all = await operation.Repositories.LlmProviders.GetAllAsync(cancellationToken);
        var accountModels = all.Where(provider => provider.OpenAiAccountId == accountId).ToList();

        foreach (var model in accountModels)
        {
            model.AccountAvailabilityCheckedAt = checkedAtUtc;
            model.AccountAvailabilityError = RefreshFailureMessage;
            model.UpdatedAt = checkedAtUtc;
            operation.Repositories.LlmProviders.Update(model);
        }

        account.LastCatalogRefreshAt = checkedAtUtc;
        account.LastCatalogRefreshError = RefreshFailureMessage;
        account.UpdatedAt = checkedAtUtc;
        operation.Repositories.OpenAiAccounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
        return Resolve(accountModels);
    }

    private static IReadOnlyList<LlmProvider> Resolve(IEnumerable<LlmProvider> providers) =>
        providers
            .Select(provider =>
            {
                provider.ResolvedMetadata = OpenAiAccountModelCatalog.Resolve(provider);
                return provider;
            })
            .OrderByDescending(provider => provider.ModelOrigin == LlmModelOrigin.BundledCatalog
                && OpenAiAccountModelCatalog.Find(provider.ModelId)?.IsPreferred == true)
            .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.Id)
            .ToList();
}

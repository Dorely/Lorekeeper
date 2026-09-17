using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Llm;

public sealed class OpenAiAccountModelCatalogService(
    IAppDatabaseOperationFactory database) : IOpenAiAccountModelCatalogService
{
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

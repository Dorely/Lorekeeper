using Lorekeeper.Authorization;
using Lorekeeper.Models;
using Lorekeeper.Persistence;

namespace Lorekeeper.Llm;

public sealed class OpenAiAccountModelCatalogService(
    IAppDatabaseOperationFactory database,
    IOpenAiAccountTokenService accountTokens,
    IOpenAiAccountModelDiscovery discovery,
    ILogger<OpenAiAccountModelCatalogService> logger) : IOpenAiAccountModelCatalogService
{
    private const string GenericRefreshError =
        "The account's model list could not be read. Use Refresh models to try again.";

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
        var endpoint = CodexProvider.ResponsesEndpointFor(account.Kind);

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
                existing.EndpointUrl = endpoint;
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
                EndpointUrl = endpoint,
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

    public async Task<OpenAiAccountModelRefreshResult> RefreshAvailabilityAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCatalogAsync(accountId, cancellationToken);

        IReadOnlyList<DiscoveredAccountModel> discovered;
        try
        {
            var access = await accountTokens.GetValidAccessAsync(accountId, cancellationToken)
                ?? throw new OpenAiAccountModelDiscoveryException("Connect the OpenAI account before refreshing its models.");
            discovered = await discovery.DiscoverAsync(access, cancellationToken);
            if (discovered.Count == 0)
                throw new OpenAiAccountModelDiscoveryException("The account did not report any selectable models. Use Refresh models to try again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is OpenAiAccountModelDiscoveryException ? ex.Message : GenericRefreshError;
            logger.LogWarning("OpenAI account {AccountId} model refresh failed: {ErrorType}", accountId, ex.GetType().Name);
            await RecordRefreshFailureAsync(
                accountId,
                message,
                ex is OpenAiAccountModelDiscoveryException { PlanNotEligible: true },
                cancellationToken);
            return new OpenAiAccountModelRefreshResult(false, 0, 0, 0, message);
        }

        return await ReconcileAsync(accountId, discovered, cancellationToken);
    }

    private async Task<OpenAiAccountModelRefreshResult> ReconcileAsync(
        int accountId,
        IReadOnlyList<DiscoveredAccountModel> discovered,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var accounts = operation.Repositories.OpenAiAccounts;
        var account = await accounts.GetByIdAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException("The OpenAI account was not found.");
        var providers = operation.Repositories.LlmProviders;
        var all = await providers.GetAllAsync(cancellationToken);
        var accountModels = all.Where(provider => provider.OpenAiAccountId == accountId).ToList();
        var firstRefresh = account.LastModelRefreshAt is null;
        var now = DateTime.UtcNow;
        var bySlug = discovered
            .GroupBy(model => model.Slug, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(model => model.Priority).First(), StringComparer.Ordinal);

        foreach (var row in accountModels.Where(IsAccountListed))
        {
            if (bySlug.TryGetValue(row.ModelId, out var listed))
            {
                row.AccountAvailability = LlmAccountModelAvailability.Available;
                if (row.ModelOrigin == LlmModelOrigin.AccountDiscovered)
                {
                    row.DisplayName = listed.DisplayName;
                    row.AccountModelMetadataJson = OpenAiAccountModelCatalog.SerializeDiscoveredMetadata(ToMetadata(listed));
                }
            }
            else
            {
                row.AccountAvailability = LlmAccountModelAvailability.Unavailable;
            }

            row.AccountAvailabilityCheckedAt = now;
            row.UpdatedAt = now;
            providers.Update(row);
        }

        foreach (var listed in bySlug.Values.OrderBy(model => model.Priority))
        {
            if (OpenAiAccountModelCatalog.Find(listed.Slug) is not null
                || accountModels.Any(row => string.Equals(row.ModelId, listed.Slug, StringComparison.Ordinal)))
            {
                continue;
            }

            var providerName = OpenAiAccountModelCatalog.ProviderName(accountId, listed.Slug);
            if (all.Any(provider => string.Equals(provider.Name, providerName, StringComparison.Ordinal)))
                providerName += "-discovered";
            if (all.Any(provider => string.Equals(provider.Name, providerName, StringComparison.Ordinal)))
            {
                logger.LogWarning("Skipped discovered OpenAI account model {ModelId}: provider name conflict.", listed.Slug);
                continue;
            }

            var model = new LlmProvider
            {
                Name = providerName,
                DisplayName = listed.DisplayName,
                EndpointUrl = CodexProvider.ResponsesEndpointFor(account.Kind),
                ModelId = listed.Slug,
                AuthType = AuthType.OAuth,
                OpenAiAccountId = accountId,
                ModelOrigin = LlmModelOrigin.AccountDiscovered,
                AccountAvailability = LlmAccountModelAvailability.Available,
                AccountAvailabilityCheckedAt = now,
                AccountModelMetadataJson = OpenAiAccountModelCatalog.SerializeDiscoveredMetadata(ToMetadata(listed)),
                CreatedAt = now,
                UpdatedAt = now,
            };
            await providers.AddAsync(model, cancellationToken);
            all.Add(model);
            accountModels.Add(model);
        }

        if (firstRefresh)
            CorrectAutomaticDefault(all, accountModels, bySlug);

        account.LastModelRefreshAt = now;
        account.LastModelRefreshError = null;
        account.UpdatedAt = now;
        accounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);

        var listedRows = accountModels.Where(IsAccountListed).ToList();
        return new OpenAiAccountModelRefreshResult(
            true,
            listedRows.Count(row => row.AccountAvailability == LlmAccountModelAvailability.Available),
            listedRows.Count(row => row.AccountAvailability == LlmAccountModelAvailability.Unavailable),
            listedRows.Count(row => row.ModelOrigin == LlmModelOrigin.AccountDiscovered),
            null);
    }

    /// <summary>
    /// The first successful refresh may move a default that Lorekeeper chose
    /// automatically: none existed, or the catalog default is not on this plan.
    /// Later refreshes never replace the default; the UI offers a switch instead.
    /// </summary>
    private static void CorrectAutomaticDefault(
        List<LlmProvider> all,
        List<LlmProvider> accountModels,
        IReadOnlyDictionary<string, DiscoveredAccountModel> bySlug)
    {
        var currentDefault = all.FirstOrDefault(provider => provider.IsDefault);
        var replaceable = currentDefault is null
            || (accountModels.Contains(currentDefault)
                && currentDefault.AccountAvailability == LlmAccountModelAvailability.Unavailable);
        if (!replaceable || OpenAiAccountModelCatalog.BestAvailable(accountModels, bySlug.ToDictionary(pair => pair.Key, pair => pair.Value.Priority, StringComparer.Ordinal)) is not { } best)
            return;

        if (currentDefault is not null)
        {
            currentDefault.IsDefault = false;
            currentDefault.UpdatedAt = DateTime.UtcNow;
        }

        best.IsDefault = true;
        best.UpdatedAt = DateTime.UtcNow;
    }

    private async Task RecordRefreshFailureAsync(
        int accountId,
        string message,
        bool planNotEligible,
        CancellationToken cancellationToken)
    {
        await using var operation = await database.OpenWriteAsync(cancellationToken);
        var accounts = operation.Repositories.OpenAiAccounts;
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
            return;

        account.LastModelRefreshError = message;
        if (planNotEligible && account.Kind == OpenAiAccountKind.ChatGptPlan)
            account.PlanIneligibleAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
        accounts.Update(account);
        await operation.SaveChangesAsync(cancellationToken);
    }

    private static bool IsAccountListed(LlmProvider provider) =>
        OpenAiAccountModelCatalog.IsAccountListed(provider);

    private static OpenAiAccountDiscoveredModelMetadata ToMetadata(DiscoveredAccountModel model) =>
        new(
            model.SupportedEfforts,
            model.DefaultEffort,
            model.Capabilities,
            model.ContextWindowTokens is int window
                ? (int)Math.Floor(window * Math.Clamp(model.EffectiveContextPercent, 1, 100) / 100d)
                : null,
            model.Priority);

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

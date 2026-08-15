using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Search;

public sealed class SearchProviderService(
IAppDatabaseOperationFactory database, IWebSearchProviderFactory factory) : ISearchProviderService
{
    public async Task<List<SearchProvider>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.SearchProviders;
        return await providers.GetAllAsync(cancellationToken);
    }
    public async Task<SearchProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.SearchProviders;
        return await providers.GetByIdAsync(id, cancellationToken);
    }
    public async Task<SearchProvider?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.SearchProviders;
        return await providers.GetActiveAsync(cancellationToken);
    }
    public async Task<bool> HasActiveProviderAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var providers = databaseOperation.Repositories.SearchProviders;
        var active = await providers.GetActiveAsync(cancellationToken);
        return active is not null && !string.IsNullOrWhiteSpace(active.ApiKey);
    }

    public async Task<SearchProvider> CreateAsync(SearchProvider provider, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.SearchProviders;
        Normalize(provider);
        Validate(provider);
        var shouldActivate = provider.IsActive;
        provider.IsActive = false;
        provider.CreatedAt = DateTime.UtcNow;
        provider.UpdatedAt = DateTime.UtcNow;
        await providers.AddAsync(provider, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);

        if (shouldActivate)
            await SetActiveAsync(provider.Id, cancellationToken);

        return provider;
    }

    public async Task<SearchProvider> UpdateAsync(SearchProvider provider, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.SearchProviders;
        Normalize(provider);
        Validate(provider);
        var shouldActivate = provider.IsActive;
        provider.UpdatedAt = DateTime.UtcNow;
        if (!shouldActivate)
        {
            providers.Update(provider);
            await databaseOperation.SaveChangesAsync(cancellationToken);
            return provider;
        }

        provider.IsActive = false;
        providers.Update(provider);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await SetActiveAsync(provider.Id, cancellationToken);
        return provider;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.SearchProviders;
        var provider = await providers.GetByIdAsync(id, cancellationToken);
        if (provider is null) return;

        providers.Remove(provider);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task SetActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var providers = databaseOperation.Repositories.SearchProviders;
        var provider = await providers.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Search provider {id} was not found.");
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("Search provider API key is required before it can be active.");

        await providers.SetActiveAsync(id, cancellationToken);
        await databaseOperation.SaveChangesAsync(cancellationToken);
    }

    public async Task<SearchProviderTestResult> TestAsync(SearchProvider provider, CancellationToken cancellationToken = default)
    {
        Normalize(provider);
        Validate(provider);
        try
        {
            var client = factory.Create(provider);
            var response = await client.SearchAsync(provider, new WebSearchRequest("Lorekeeper writing research", 3), cancellationToken);
            return response.Results.Count > 0
                ? new SearchProviderTestResult(true, $"{ProviderLabel(provider)} returned {response.Results.Count} result(s).")
                : new SearchProviderTestResult(true, $"{ProviderLabel(provider)} responded successfully, but returned no results.");
        }
        catch (Exception ex)
        {
            return new SearchProviderTestResult(false, ex.Message);
        }
    }

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
    {
        SearchProvider active;
        await using (var readOperation = await database.OpenReadAsync(cancellationToken))
        {
            active = await readOperation.Repositories.SearchProviders.GetActiveAsync(cancellationToken)
                ?? throw new InvalidOperationException("No active search provider is configured.");
        }
        if (string.IsNullOrWhiteSpace(active.ApiKey))
            throw new InvalidOperationException("The active search provider does not have an API key.");

        var client = factory.Create(active);
        return await client.SearchAsync(active, request, cancellationToken);
    }

    private static void Normalize(SearchProvider provider)
    {
        provider.Name = (provider.Name ?? string.Empty).Trim();
        provider.DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? null : provider.DisplayName.Trim();
        provider.ApiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? null : provider.ApiKey.Trim();
        provider.ConfigurationJson = string.IsNullOrWhiteSpace(provider.ConfigurationJson) ? "{}" : provider.ConfigurationJson.Trim();
    }

    private static void Validate(SearchProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Name))
            throw new ArgumentException("Provider name is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new ArgumentException("API key is required.", nameof(provider));
        if (provider.ConfigurationJson.TrimStart().StartsWith('{') is false)
            throw new ArgumentException("Configuration JSON must be a JSON object.", nameof(provider));
    }

    private static string ProviderLabel(SearchProvider provider) =>
        provider.DisplayName ?? provider.Name;
}

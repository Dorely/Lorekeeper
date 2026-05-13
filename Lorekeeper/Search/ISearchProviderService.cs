using Lorekeeper.Models;

namespace Lorekeeper.Search;

public interface ISearchProviderService
{
    Task<List<SearchProvider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SearchProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<SearchProvider?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<bool> HasActiveProviderAsync(CancellationToken cancellationToken = default);
    Task<SearchProvider> CreateAsync(SearchProvider provider, CancellationToken cancellationToken = default);
    Task<SearchProvider> UpdateAsync(SearchProvider provider, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task SetActiveAsync(int id, CancellationToken cancellationToken = default);
    Task<SearchProviderTestResult> TestAsync(SearchProvider provider, CancellationToken cancellationToken = default);
    Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default);
}
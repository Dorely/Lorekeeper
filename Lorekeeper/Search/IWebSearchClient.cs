using Lorekeeper.Models;

namespace Lorekeeper.Search;

public interface IWebSearchClient
{
    SearchProviderKind ProviderKind { get; }
    Task<WebSearchResponse> SearchAsync(SearchProvider provider, WebSearchRequest request, CancellationToken cancellationToken = default);
}
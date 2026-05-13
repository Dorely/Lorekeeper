using Lorekeeper.Models;

namespace Lorekeeper.Search;

public sealed class WebSearchProviderFactory(IEnumerable<IWebSearchClient> clients) : IWebSearchProviderFactory
{
    public IWebSearchClient Create(SearchProvider provider) =>
        clients.FirstOrDefault(client => client.ProviderKind == provider.ProviderKind)
        ?? throw new InvalidOperationException($"Search provider kind '{provider.ProviderKind}' is not supported.");
}
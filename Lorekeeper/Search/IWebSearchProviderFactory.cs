using Lorekeeper.Models;

namespace Lorekeeper.Search;

public interface IWebSearchProviderFactory
{
    IWebSearchClient Create(SearchProvider provider);
}
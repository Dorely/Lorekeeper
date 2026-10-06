namespace Lorekeeper.Search;

/// <summary>
/// Web search built into a chat model's own provider, offered by its chat client through
/// <see cref="Microsoft.Extensions.AI.IChatClient.GetService"/>. Results are the pages the
/// provider's search actually returned or cited; the model never supplies URLs itself.
/// </summary>
public interface IModelWebSearch
{
    string DisplayName { get; }

    Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default);
}

using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IOpenAiAccountModelCatalogService
{
    Task<IReadOnlyList<LlmProvider>> EnsureCatalogAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

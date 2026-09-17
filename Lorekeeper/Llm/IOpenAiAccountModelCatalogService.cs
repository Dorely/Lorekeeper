using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public sealed record AccountModelCatalogRefreshResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<LlmProvider> Models);

public interface IOpenAiAccountModelCatalogService
{
    Task<IReadOnlyList<LlmProvider>> EnsureCatalogAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<AccountModelCatalogRefreshResult> RefreshAvailabilityAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmProvider>> ReconcileAvailabilityAsync(
        int accountId,
        IReadOnlyList<LlmDiscoveredModel> discoveredModels,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmProvider>> RecordRefreshFailureAsync(
        int accountId,
        DateTime checkedAtUtc,
        CancellationToken cancellationToken = default);
}

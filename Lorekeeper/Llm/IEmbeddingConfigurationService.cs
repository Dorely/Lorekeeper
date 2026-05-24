using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IEmbeddingConfigurationService
{
    Task<EmbeddingConfiguration?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LlmProvider>> ListConnectionProvidersAsync(CancellationToken cancellationToken = default);
    Task<EmbeddingTestResult> TestAsync(EmbeddingTestRequest request, CancellationToken cancellationToken = default);
    Task<EmbeddingSaveResult> SaveAsync(EmbeddingConfigurationDraft draft, CancellationToken cancellationToken = default);
    Task<bool> ConfigureCodexDefaultIfUnsetAsync(int providerId, CancellationToken cancellationToken = default);
    Task<EmbeddingUnsetResult> UnsetAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

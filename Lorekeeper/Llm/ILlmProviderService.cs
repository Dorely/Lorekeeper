using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface ILlmProviderService
{
    Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task<LlmProvider> CreateAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    Task<LlmProvider> UpdateAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task SetDefaultAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the API key (or OAuth access token) that should be used to call <paramref name="providerId"/>.
    /// Follows <see cref="LlmProvider.CredentialSourceId"/> for child model rows.
    /// </summary>
    Task<string?> GetEffectiveApiKeyAsync(int providerId, CancellationToken cancellationToken = default);
}

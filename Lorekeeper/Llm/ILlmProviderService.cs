using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface ILlmProviderService
{
    Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetDefaultChatProviderAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<VisionProviderAvailability> GetDefaultVisionProviderAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<List<LlmProvider>> ListWorkingChatProvidersAsync(CancellationToken cancellationToken = default);
    Task<List<LlmProvider>> ListWorkingVisionProvidersAsync(CancellationToken cancellationToken = default);
    Task<bool> IsChatProviderWorkingAsync(int providerId, CancellationToken cancellationToken = default);
    Task<bool> IsVisionProviderWorkingAsync(int providerId, CancellationToken cancellationToken = default);
    Task<bool> IsCodexConnectedAsync(CancellationToken cancellationToken = default);
    Task<LlmProvider> CreateAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    Task<LlmProvider> UpdateAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    Task UpdateConnectionAsync(LlmConnectionUpdate update, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task DeleteConnectionAsync(int id, CancellationToken cancellationToken = default);
    Task SetDefaultAsync(int id, CancellationToken cancellationToken = default);
    Task<LlmProvider> MarkChatTestSucceededAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    Task<LlmProvider> MarkChatTestFailedAsync(LlmProvider provider, string error, CancellationToken cancellationToken = default);
    Task<LlmProvider> MarkVisionTestSucceededAsync(LlmProvider provider, CancellationToken cancellationToken = default);
    Task<LlmProvider> MarkVisionTestFailedAsync(LlmProvider provider, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the API key (or OAuth access token) that should be used to call <paramref name="providerId"/>.
    /// Follows <see cref="LlmProvider.CredentialSourceId"/> for child model rows.
    /// </summary>
    Task<string?> GetEffectiveApiKeyAsync(int providerId, CancellationToken cancellationToken = default);
}

public sealed record LlmConnectionUpdate(
    int Id,
    string? DisplayName,
    string EndpointUrl,
    AuthType AuthType,
    string? ApiKey);

public sealed record ChatProviderAvailability(
    bool IsAvailable,
    LlmProvider? Provider,
    string Message)
{
    public static ChatProviderAvailability Available(LlmProvider provider) =>
        new(true, provider, string.Empty);

    public static ChatProviderAvailability Unavailable(string message, LlmProvider? provider = null) =>
        new(false, provider, message);
}

public sealed record VisionProviderAvailability(
    bool IsAvailable,
    LlmProvider? Provider,
    string Message)
{
    public static VisionProviderAvailability Available(LlmProvider provider) =>
        new(true, provider, string.Empty);

    public static VisionProviderAvailability Unavailable(string message, LlmProvider? provider = null) =>
        new(false, provider, message);
}

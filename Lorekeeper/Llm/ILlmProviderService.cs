using Lorekeeper.Authorization;
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
    Task<IReadOnlyList<ChatModelOption>> ListChatModelOptionsAsync(CancellationToken cancellationToken = default);
    Task<ChatModelSelection> ResolveChatModelSelectionAsync(int? selectedProviderId, CancellationToken cancellationToken = default);
    Task<int?> NormalizeChatModelSelectionAsync(int? selectedProviderId, CancellationToken cancellationToken = default);
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
    /// Returns the API key (or account-owned OAuth access token) used to call
    /// <paramref name="providerId"/>. Manual connection children follow
    /// <see cref="LlmProvider.CredentialSourceId"/>; account rows use
    /// <see cref="LlmProvider.OpenAiAccountId"/>.
    /// </summary>
    Task<string?> GetEffectiveApiKeyAsync(int providerId, CancellationToken cancellationToken = default);
    Task<OpenAiAccountAccess?> GetOpenAiAccountAccessAsync(int accountId, CancellationToken cancellationToken = default);
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

/// <summary>
/// A saved provider/model row that is currently usable for chat. Manual rows
/// require a successful credential-valid readiness snapshot; bundled OpenAI
/// account rows derive readiness from credentials and local catalog validation.
/// The labels identify both the connection and model.
/// </summary>
public sealed record ChatModelOption(
    int ProviderId,
    string ConnectionLabel,
    string ModelLabel,
    string Label,
    bool IsDefault,
    LlmProvider Provider)
{
    public bool IsAvailable => true;
}

/// <summary>
/// Resolves one conversation's nullable override to the exact provider row that
/// will be used for its next turn. Explicit overrides never fall back to another
/// row; a missing or no-longer-working row is returned as an unavailable override.
/// </summary>
public sealed record ChatModelSelection(
    int? SelectedProviderId,
    LlmProvider? Provider,
    string? ConnectionLabel,
    string? ModelLabel,
    string? Label,
    bool IsAvailable,
    bool IsDefault,
    bool IsOverride,
    bool IsUnavailableOverride,
    string Message)
{
    public int? ProviderId => Provider?.Id;
}

public static class ChatModelSelectionMessages
{
    public const string Changed =
        "The chat model selection changed before this turn started. Choose the current model in the picker and try again.";
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

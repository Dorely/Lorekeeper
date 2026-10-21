using Lorekeeper.Authorization;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

/// <summary>
/// One model the signed-in ChatGPT account reports as selectable. Lower
/// <see cref="Priority"/> values sort first, matching the account's own picker.
/// </summary>
public sealed record DiscoveredAccountModel(
    string Slug,
    string DisplayName,
    int Priority,
    IReadOnlyList<LlmReasoningEffort> SupportedEfforts,
    LlmReasoningEffort? DefaultEffort,
    LlmModelCapabilities Capabilities,
    int? ContextWindowTokens,
    int EffectiveContextPercent);

/// <summary>
/// Reads the model list offered to one ChatGPT account. Implementations make a
/// network call and never persist anything; reconciliation is owned by
/// <see cref="IOpenAiAccountModelCatalogService"/>.
/// </summary>
public interface IOpenAiAccountModelDiscovery
{
    Task<IReadOnlyList<DiscoveredAccountModel>> DiscoverAsync(
        OpenAiAccountAccess access,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiAccountModelDiscoveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

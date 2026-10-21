using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public interface IOpenAiAccountModelCatalogService
{
    Task<IReadOnlyList<LlmProvider>> EnsureCatalogAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the connected account which models it offers and reconciles the
    /// account's catalog and discovered rows. A failed refresh keeps the last
    /// known availability and records a sanitized error on the account.
    /// </summary>
    Task<OpenAiAccountModelRefreshResult> RefreshAvailabilityAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

public sealed record OpenAiAccountModelRefreshResult(
    bool Succeeded,
    int AvailableCount,
    int UnavailableCount,
    int DiscoveredCount,
    string? Error);

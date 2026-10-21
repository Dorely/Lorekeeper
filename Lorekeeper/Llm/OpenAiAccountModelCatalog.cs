using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Models;

namespace Lorekeeper.Llm;

public sealed record OpenAiAccountModelCatalogEntry(
    string ModelId,
    string DisplayName,
    bool IsPreferred,
    LlmReasoningEffort DefaultEffort,
    IReadOnlyList<LlmReasoningEffort> SupportedEfforts,
    LlmModelCapabilities Capabilities,
    int UsableInputBudgetTokens);

/// <summary>
/// Account-reported metadata persisted for <see cref="LlmModelOrigin.AccountDiscovered"/>
/// rows. It never contains credentials.
/// </summary>
public sealed record OpenAiAccountDiscoveredModelMetadata(
    IReadOnlyList<LlmReasoningEffort> SupportedEfforts,
    LlmReasoningEffort? DefaultEffort,
    LlmModelCapabilities Capabilities,
    int? UsableInputBudgetTokens,
    int Priority);

public static class OpenAiAccountModelCatalog
{
    public const int SchemaVersion = 3;
    public const string Source = "https://learn.chatgpt.com/docs/models";
    public const int UsableInputBudgetTokens = 272_000;
    public static readonly DateOnly ValidationDate = new(2026, 9, 29);
    public const string DiscoverySource = "chatgpt-account-model-list";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyList<LlmReasoningEffort> SupportedEfforts =
    [
        LlmReasoningEffort.Low,
        LlmReasoningEffort.Medium,
        LlmReasoningEffort.High,
        LlmReasoningEffort.ExtraHigh,
        LlmReasoningEffort.Maximum,
    ];

    public static readonly IReadOnlyList<OpenAiAccountModelCatalogEntry> Entries =
    [
        Entry("gpt-6.1-sol", "6.1 Sol", true, LlmReasoningEffort.Medium),
        Entry("gpt-6-astra", "Astra", false, LlmReasoningEffort.Medium),
        Entry("gpt-6-sol", "6 Sol", false, LlmReasoningEffort.Medium),
        Entry("gpt-6-luna", "6 Luna", false, LlmReasoningEffort.Medium),
        Entry("gpt-5.6-sol", "5.6 Sol", false, LlmReasoningEffort.Low),
        Entry("gpt-5.6-terra", "Terra", false, LlmReasoningEffort.Medium),
        Entry("gpt-5.6-luna", "5.6 Luna", false, LlmReasoningEffort.Medium),
    ];

    public static OpenAiAccountModelCatalogEntry Preferred =>
        Entries.Single(entry => entry.IsPreferred);

    public static OpenAiAccountModelCatalogEntry? Find(string? modelId) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.ModelId, modelId, StringComparison.Ordinal));

    public static string ProviderName(int accountId, string modelId) =>
        $"openai-account-{accountId}-{modelId}";

    public static LlmProviderResolvedMetadata Resolve(LlmProvider provider)
    {
        if (provider.OpenAiAccountId is int accountId
            && provider.ModelOrigin == LlmModelOrigin.BundledCatalog
            && Find(provider.ModelId) is { } entry)
        {
            return new LlmProviderResolvedMetadata(
                accountId,
                provider.ModelOrigin,
                entry.SupportedEfforts,
                entry.Capabilities,
                provider.MaxInputTokens ?? entry.UsableInputBudgetTokens,
                provider.ReasoningEffort ?? entry.DefaultEffort,
                SchemaVersion,
                Source,
                ValidationDate);
        }

        if (provider.OpenAiAccountId is int discoveredAccountId
            && provider.ModelOrigin == LlmModelOrigin.AccountDiscovered
            && ReadDiscoveredMetadata(provider) is { } discovered)
        {
            return new LlmProviderResolvedMetadata(
                discoveredAccountId,
                provider.ModelOrigin,
                discovered.SupportedEfforts,
                discovered.Capabilities,
                provider.MaxInputTokens ?? discovered.UsableInputBudgetTokens,
                provider.ReasoningEffort ?? discovered.DefaultEffort,
                null,
                DiscoverySource,
                provider.AccountAvailabilityCheckedAt is { } checkedAt ? DateOnly.FromDateTime(checkedAt) : null);
        }

        return new LlmProviderResolvedMetadata(
            provider.OpenAiAccountId,
            provider.ModelOrigin,
            [],
            LlmModelCapabilities.None,
            provider.MaxInputTokens,
            provider.ReasoningEffort,
            null,
            null,
            null);
    }

    public static bool IsAccountListed(LlmProvider provider) =>
        provider.ModelOrigin is LlmModelOrigin.BundledCatalog or LlmModelOrigin.AccountDiscovered;

    /// <summary>
    /// The best usable account model: the catalog's preferred model when the
    /// account offers it, then the account's own ordering, then catalog order.
    /// </summary>
    public static LlmProvider? BestAvailable(
        IEnumerable<LlmProvider> accountModels,
        IReadOnlyDictionary<string, int>? listedPriorities = null) =>
        accountModels
            .Where(IsAccountListed)
            .Where(row => row.AccountAvailability == LlmAccountModelAvailability.Available)
            .Where(row => TryValidateEffort(row, out _))
            .Where(row => Resolve(row).Capabilities.HasFlag(LlmModelCapabilities.TextInput))
            .OrderByDescending(row => row.ModelOrigin == LlmModelOrigin.BundledCatalog
                && Find(row.ModelId)?.IsPreferred == true)
            .ThenBy(row => listedPriorities is not null && listedPriorities.TryGetValue(row.ModelId, out var priority)
                ? priority
                : ReadDiscoveredMetadata(row)?.Priority ?? int.MaxValue)
            .ThenBy(row => CatalogIndex(row.ModelId))
            .ThenBy(row => row.Id)
            .FirstOrDefault();

    private static int CatalogIndex(string modelId)
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            if (string.Equals(Entries[index].ModelId, modelId, StringComparison.Ordinal))
                return index;
        }

        return int.MaxValue;
    }

    public static string SerializeDiscoveredMetadata(OpenAiAccountDiscoveredModelMetadata metadata) =>
        JsonSerializer.Serialize(metadata, MetadataJsonOptions);

    public static OpenAiAccountDiscoveredModelMetadata? ReadDiscoveredMetadata(LlmProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.AccountModelMetadataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<OpenAiAccountDiscoveredModelMetadata>(provider.AccountModelMetadataJson, MetadataJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryValidateEffort(LlmProvider provider, out string? error)
    {
        if (provider.ModelOrigin == LlmModelOrigin.AccountDiscovered)
        {
            var discovered = ReadDiscoveredMetadata(provider);
            if (provider.ReasoningEffort is { } discoveredEffort
                && discovered is { SupportedEfforts.Count: > 0 }
                && !discovered.SupportedEfforts.Contains(discoveredEffort))
            {
                error = $"{provider.DisplayName ?? provider.ModelId} does not support the saved {discoveredEffort} thinking effort on this account. Choose a listed effort or the account default.";
                return false;
            }

            error = null;
            return true;
        }

        if (provider.ModelOrigin != LlmModelOrigin.BundledCatalog)
        {
            error = null;
            return true;
        }

        var entry = Find(provider.ModelId);
        if (entry is null)
        {
            error = $"Catalog model '{provider.ModelId}' is not part of catalog schema {SchemaVersion}.";
            return false;
        }

        if (provider.ReasoningEffort is { } effort && !entry.SupportedEfforts.Contains(effort))
        {
            error = $"{entry.DisplayName} does not support the saved {effort} thinking effort. Choose Low, Medium, High, Extra high, Maximum, or the catalog default.";
            return false;
        }

        error = null;
        return true;
    }

    private static OpenAiAccountModelCatalogEntry Entry(
        string modelId,
        string displayName,
        bool isPreferred,
        LlmReasoningEffort defaultEffort) =>
        new(
            modelId,
            displayName,
            isPreferred,
            defaultEffort,
            SupportedEfforts,
            LlmModelCapabilities.TextInput | LlmModelCapabilities.ImageInput,
            UsableInputBudgetTokens);
}

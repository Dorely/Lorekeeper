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

public static class OpenAiAccountModelCatalog
{
    public const int SchemaVersion = 1;
    public const string Source = "https://learn.chatgpt.com/docs/models";
    public const int UsableInputBudgetTokens = 272_000;
    public static readonly DateOnly ValidationDate = new(2026, 9, 16);

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
        Entry("gpt-6-astra", "Astra", false, LlmReasoningEffort.Medium),
        Entry("gpt-5.6-sol", "Sol", true, LlmReasoningEffort.Low),
        Entry("gpt-5.6-terra", "Terra", false, LlmReasoningEffort.Medium),
        Entry("gpt-5.6-luna", "Luna", false, LlmReasoningEffort.Medium),
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
                provider.AccountAvailability,
                entry.SupportedEfforts,
                entry.Capabilities,
                provider.DiscoveredContextWindowTokens,
                provider.MaxInputTokens ?? entry.UsableInputBudgetTokens,
                provider.ReasoningEffort ?? entry.DefaultEffort,
                SchemaVersion,
                Source,
                ValidationDate);
        }

        return new LlmProviderResolvedMetadata(
            provider.OpenAiAccountId,
            provider.ModelOrigin,
            provider.AccountAvailability,
            [],
            LlmModelCapabilities.None,
            provider.DiscoveredContextWindowTokens,
            provider.MaxInputTokens,
            provider.ReasoningEffort,
            null,
            null,
            null);
    }

    public static bool TryValidateEffort(LlmProvider provider, out string? error)
    {
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

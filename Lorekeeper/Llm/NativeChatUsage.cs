using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Llm;

internal static class NativeChatUsage
{
    public const string CacheWriteInputTokens = "CacheWriteInputTokenCount";

    public static UsageDetails Responses(JsonElement usage)
    {
        var details = new UsageDetails
        {
            InputTokenCount = Count(usage, "input_tokens"),
            OutputTokenCount = Count(usage, "output_tokens"),
            TotalTokenCount = Count(usage, "total_tokens"),
        };
        if (usage.TryGetProperty("input_tokens_details", out var input))
        {
            details.CachedInputTokenCount = Count(input, "cached_tokens");
            if (Count(input, "cache_write_tokens") is { } writes)
                (details.AdditionalCounts ??= [])[CacheWriteInputTokens] = writes;
        }
        if (usage.TryGetProperty("output_tokens_details", out var output))
            details.ReasoningTokenCount = Count(output, "reasoning_tokens");
        return details;
    }

    public static long? Count(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count : null;
}

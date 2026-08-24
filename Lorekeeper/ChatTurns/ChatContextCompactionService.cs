using Microsoft.Extensions.AI;
using Lorekeeper.Tokens;

namespace Lorekeeper.ChatTurns;

public sealed record ChatCompactionResult(
    int OriginalTokenCount,
    int FinalTokenCount,
    int MaximumInputTokens,
    string TokenCountMethod,
    IReadOnlyList<string> NewlyTombstonedCallIds,
    IReadOnlyList<string> CompletedRoundCallIds,
    bool LimitExceeded);

public interface IChatContextCompactionService
{
    ChatCompactionResult? TryCompact(IList<ChatMessage> messages, string? modelId);
}

public static class ChatContextCompaction
{
    // Retained only so rows written by older versions remain renderable. New
    // turns never create a synthetic compaction row or call.
    public const string HistoricalToolName = "Chat Compacted";

    public const string ResultTombstone =
        "Result dropped from active context to conserve tokens. Do not infer or rely on its prior contents. Re-run the tool only if this result is still needed.";

    public const string LimitExceededMessage =
        "The conversation is too large for the selected model, even after removing tool-result payloads. Reset the conversation or select a model with a larger context window, then try again.";

    private const string ToolContextMarker = "Lorekeeper.ChatTurns.ToolContext";
    private const string ResultTombstoneMarker = "Lorekeeper.ChatTurns.ResultTombstone";

    public static ChatMessage MarkToolContext(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        (message.AdditionalProperties ??= new AdditionalPropertiesDictionary())[ToolContextMarker] = true;
        return message;
    }

    public static bool IsToolContext(ChatMessage message) =>
        message.AdditionalProperties is not null
        && message.AdditionalProperties.TryGetValue(ToolContextMarker, out var value)
        && value is true;

    public static bool IsResultTombstoned(FunctionResultContent result) =>
        result.AdditionalProperties is not null
        && result.AdditionalProperties.TryGetValue(ResultTombstoneMarker, out var value)
        && value is true;

    public static void MarkResultTombstoned(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        (result.AdditionalProperties ??= new AdditionalPropertiesDictionary())[ResultTombstoneMarker] = true;
        result.Result = ResultTombstone;
    }
}

internal sealed class ChatContextCompactionService(
    ITokenCounter tokenCounter,
    ChatTokenLimitResolver tokenLimitResolver) : IChatContextCompactionService
{
    private sealed record ToolRound(
        ChatMessage AssistantMessage,
        IReadOnlySet<string> CallIds,
        IReadOnlyList<FunctionResultContent> Results,
        IReadOnlyList<ChatMessage> FollowingToolContextMessages);

    public ChatCompactionResult? TryCompact(IList<ChatMessage> messages, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var originalCount = Count(messages, modelId, out var maximumInputTokens, out var method);
        if ((long)originalCount * 100 < (long)maximumInputTokens * 90)
            return null;

        var rounds = FindToolRounds(messages);
        var eligibleResults = rounds
            .SelectMany(round => round.Results)
            .Where(result => !ChatContextCompaction.IsResultTombstoned(result))
            .ToList();
        var tombstonedCallIds = new List<string>();
        var completedRoundCallIds = new List<string>();
        var finalCount = originalCount;

        // Replace one result at a time and recount after every replacement.
        foreach (var result in eligibleResults)
        {
            ChatContextCompaction.MarkResultTombstoned(result);
            tombstonedCallIds.Add(result.CallId);
            finalCount = Count(messages, modelId, out _, out _);
            foreach (var round in rounds)
            {
                if (round.Results.Count > 0
                    && round.Results.All(ChatContextCompaction.IsResultTombstoned)
                    && round.Results
                        .Select(result => result.CallId)
                        .ToHashSet(StringComparer.Ordinal)
                        .SetEquals(round.CallIds)
                    && DropCompletedRoundContext(messages, round))
                {
                    completedRoundCallIds.AddRange(round.CallIds);
                }
            }

            // Cleanup can remove reasoning and visual payloads, so recount the
            // post-cleanup request before deciding whether to continue.
            finalCount = Count(messages, modelId, out _, out _);
            if ((long)finalCount * 100 < (long)maximumInputTokens * 90)
                break;
        }

        var limitExceeded = (long)finalCount * 100 >= (long)maximumInputTokens * 90;
        return new ChatCompactionResult(
            OriginalTokenCount: originalCount,
            FinalTokenCount: finalCount,
            MaximumInputTokens: maximumInputTokens,
            TokenCountMethod: method,
            NewlyTombstonedCallIds: tombstonedCallIds,
            CompletedRoundCallIds: completedRoundCallIds,
            LimitExceeded: limitExceeded);
    }

    private int Count(
        IEnumerable<ChatMessage> messages,
        string? modelId,
        out int maximumInputTokens,
        out string method)
    {
        var result = tokenCounter.Count(
            ChatModelHistory.FormatForTokenCount(messages),
            new TokenCountRequest(modelId));
        maximumInputTokens = Math.Max(1, tokenLimitResolver.Resolve(modelId));
        method = result.Method;
        return result.TokenCount;
    }

    private static IReadOnlyList<ToolRound> FindToolRounds(IList<ChatMessage> messages)
    {
        var rounds = new List<ToolRound>();
        for (var index = 0; index < messages.Count; index++)
        {
            var assistant = messages[index];
            if (assistant.Role != ChatRole.Assistant)
                continue;

            var callIds = assistant.Contents
                .OfType<FunctionCallContent>()
                .Select(call => call.CallId)
                .Where(callId => !string.IsNullOrWhiteSpace(callId))
                .ToHashSet(StringComparer.Ordinal);
            if (callIds.Count == 0)
                continue;

            var results = new List<FunctionResultContent>();
            var visuals = new List<ChatMessage>();
            for (var next = index + 1; next < messages.Count; next++)
            {
                var following = messages[next];
                if (following.Role == ChatRole.Tool)
                {
                    results.AddRange(following.Contents
                        .OfType<FunctionResultContent>()
                        .Where(result => callIds.Contains(result.CallId)));
                    continue;
                }

                if (ChatContextCompaction.IsToolContext(following))
                {
                    visuals.Add(following);
                    continue;
                }

                break;
            }

            if (results.Count > 0)
                rounds.Add(new ToolRound(assistant, callIds, results, visuals));
        }

        return rounds;
    }

    private static bool DropCompletedRoundContext(IList<ChatMessage> messages, ToolRound round)
    {
        var removedReasoning = round.AssistantMessage.Contents.Any(content => content is TextReasoningContent);
        round.AssistantMessage.Contents = round.AssistantMessage.Contents
            .Where(content => content is not TextReasoningContent)
            .ToList();

        var removedVisual = false;
        foreach (var visual in round.FollowingToolContextMessages)
            removedVisual |= messages.Remove(visual);

        return removedReasoning || removedVisual;
    }
}

using Lorekeeper.Llm;
using Microsoft.Extensions.AI;

namespace Lorekeeper.ChatTurns;

/// <summary>Builds one fresh workspace after the durable conversational prefix.</summary>
public static class ChatRequestLayout
{
    private const string Canonical = "Lorekeeper.CanonicalConversation";
    private const string CanonicalUser = "Lorekeeper.CanonicalUser";
    private const string Boundary = "Lorekeeper.CacheBoundary";
    private const string Lifetime = "Lorekeeper.PromptLifetime";
    private const string Identity = "Lorekeeper.RequestIdentity";
    private const string CacheKeyProperty = "Lorekeeper.PromptCacheKey";

    public static ChatMessage MarkCanonical(ChatMessage message, bool isUser = false)
    {
        (message.AdditionalProperties ??= [])[Canonical] = true;
        message.AdditionalProperties[CanonicalUser] = isUser;
        return message;
    }

    public static bool IsCanonical(ChatMessage message) => message.AdditionalProperties?.TryGetValue(Canonical, out var value) is true && value is true;
    private static bool IsCanonicalUser(ChatMessage message) => message.AdditionalProperties?.TryGetValue(CanonicalUser, out var value) is true && value is true;
    public static bool IsBoundary(ChatMessage message) => message.AdditionalProperties?.TryGetValue(Boundary, out var value) is true && value is true;
    public static bool IsApplication(ChatMessage message) => message.AdditionalProperties?.TryGetValue(Lifetime, out var value) is true && value is PromptLifetime.StableApplication;
    public static string? RequestIdentity(ChatMessage message) => message.AdditionalProperties?.TryGetValue(Identity, out var value) is true ? value as string : null;

    /// <summary>The conversation's stable cache-routing key, carried by its application instructions.</summary>
    public static string? CacheKey(IEnumerable<ChatMessage> messages) => messages
        .Select(message => message.AdditionalProperties?.TryGetValue(CacheKeyProperty, out var value) is true ? value as string : null)
        .FirstOrDefault(key => key is not null);

    public static void Initialize(List<ChatMessage> messages, ChatPrompt prompt, string userText, Guid conversationId, string surface)
    {
        // Adapters have loaded history in order, ending with the newly persisted user row.
        var current = messages[^1];
        var history = messages.Skip(1).Take(messages.Count - 2).ToList();
        var canonical = history.Where(IsCanonical).ToList();
        var workspace = history.Where(message => !IsCanonical(message)).ToList();
        var attachments = current.Contents.Skip(1).ToList();
        if (attachments.Count > 0) workspace.Add(new ChatMessage(ChatRole.User, attachments));
        messages.Clear();
        var app = Section(prompt.Application, PromptLifetime.StableApplication);
        app.AdditionalProperties![Identity] = $"{surface}:{conversationId:N}";
        app.AdditionalProperties[CacheKeyProperty] = conversationId.ToString("D");
        messages.Add(app);
        if (!string.IsNullOrWhiteSpace(prompt.Project)) messages.Add(Section(prompt.Project, PromptLifetime.StableProject));
        messages.AddRange(canonical);
        messages.Add(MarkCanonical(new ChatMessage(ChatRole.User, userText), isUser: true));
        if (!string.IsNullOrWhiteSpace(prompt.Turn)) messages.Add(Section(prompt.Turn, PromptLifetime.TurnEphemeral));
        foreach (var message in workspace)
        {
            (message.AdditionalProperties ??= [])[Lifetime] = PromptLifetime.TurnEphemeral;
            messages.Add(message);
        }
    }

    /// <summary>Recompute endpoints after every complete tool batch or trim; never mutate the transcript.</summary>
    public static IReadOnlyList<ChatMessage> Prepare(IEnumerable<ChatMessage> source)
    {
        var messages = source.Select(Clone).ToList();
        if (!messages.Any(IsApplication)) return messages;
        var users = messages.Where(IsCanonicalUser).ToList();
        // The live workspace is reusable during this turn only, including tools and visual deliveries.
        var tail = messages.LastOrDefault(message => message.Role != ChatRole.Assistant);
        // The previous request ended at the last non-assistant row before the newest assistant
        // message: on a turn's first request that is the replayed history, later the prior tool
        // round. Re-marking it reads that cache entry exactly, however many blocks a round adds.
        var lastAssistant = messages.FindLastIndex(message => message.Role == ChatRole.Assistant);
        var previousTail = messages.Take(Math.Max(lastAssistant, 0)).LastOrDefault(message => message.Role != ChatRole.Assistant);
        ChatMessage?[] candidates = [messages.First(IsApplication), users.LastOrDefault(), previousTail, tail, users.Count > 1 ? users[^2] : null];
        foreach (var message in candidates.OfType<ChatMessage>().Distinct().Take(4))
            (message.AdditionalProperties ??= [])[Boundary] = true;
        return messages;
    }

    private static ChatMessage Clone(ChatMessage message) => new(message.Role, message.Contents.ToList())
    {
        AuthorName = message.AuthorName,
        MessageId = message.MessageId,
        AdditionalProperties = message.AdditionalProperties is null ? [] : new(message.AdditionalProperties),
    };

    private static ChatMessage Section(string text, PromptLifetime lifetime) => new(ChatRole.System, text)
    {
        AdditionalProperties = new() { [Lifetime] = lifetime },
    };
}

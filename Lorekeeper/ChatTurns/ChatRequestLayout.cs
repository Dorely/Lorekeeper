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
        var candidates = new List<ChatMessage> { messages.First(IsApplication) };
        candidates.AddRange(messages.Where(IsCanonicalUser).TakeLast(2));
        // The live workspace is reusable during this turn only, including tools and visual deliveries.
        var last = messages.LastOrDefault(message => message.Role != ChatRole.Assistant);
        if (last is not null && !IsCanonical(last)) candidates.Add(last);
        foreach (var message in candidates.Distinct().Take(4))
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

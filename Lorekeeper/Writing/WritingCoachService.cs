using System.Runtime.CompilerServices;
using System.Text;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Writing;

public sealed class WritingCoachService(
    IProjectRepository projects,
    IWritingCoachConversationRepository conversations,
    IProjectFactService projectFacts,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    ILogger<WritingCoachService> logger) : IWritingCoachService
{
    private const string CoachSystemPrompt = """
        You are a Writing Coach for a long-form fiction project. Your job is to help
        the writer produce writing samples in their own style and words so future AI
        drafting can better imitate their voice.

        How to work:
        - Give focused prompts, craft observations, and revision questions that help
          the writer discover and refine their own style.
        - Use the current writing sample draft as read-only context. You cannot edit,
          save, rename, delete, or otherwise change stored samples.
        - Do not claim you changed the draft or stored anything.
        - Avoid taking over the prose. When the user asks for examples, keep them short
          and frame them as options the writer can adapt.
        - Keep replies concise and practical. Prefer one next step over a broad lecture.
        - Pay attention to sentence rhythm, diction, point of view, imagery, pacing,
          and emotional texture. Help the writer make those choices intentional.
        """;

    private const string InitialAssistantGreeting =
        "Let's shape a writing sample in your own voice. What kind of scene, moment, or mood do you want to practice first?";

    public async Task<WritingCoachConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is not null) return existing;

        _ = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var conversation = new WritingCoachConversation { ProjectId = projectId };
        await conversations.AddConversationAsync(conversation, cancellationToken);

        var greeting = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = 0,
            Role = WritingCoachMessageRole.Assistant,
            Content = InitialAssistantGreeting,
            Status = WritingCoachMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(greeting, cancellationToken);
        await conversations.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<IReadOnlyList<WritingCoachMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await conversations.LoadMessagesAsync(conversationId, cancellationToken);

    public async Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await conversations.GetByProjectIdAsync(projectId, cancellationToken);
        if (existing is null) return;

        conversations.RemoveConversation(existing);
        await conversations.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<WritingCoachTurnUpdate> SendAsync(
        Guid projectId,
        string userText,
        string? currentSampleTitle,
        string? currentSampleBody,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Message cannot be empty.", nameof(userText));

        var conversation = await GetOrCreateAsync(projectId, cancellationToken);
        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;

        var userMessage = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = WritingCoachMessageRole.User,
            Content = userText.Trim(),
            Status = WritingCoachMessageStatus.Completed,
        };
        await conversations.AddMessageAsync(userMessage, cancellationToken);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(cancellationToken);

        IChatClient? chat = null;
        string? setupError = null;
        try
        {
            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");
            chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing Coach turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null || chat is null)
        {
            yield return new WritingCoachTurnError(setupError ?? "Writing Coach setup failed.", Cancelled: false);
            yield break;
        }

        var facts = await projectFacts.ListAsync(projectId, cancellationToken);
        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = BuildMessages(history, userMessage.Id, userMessage.Content, currentSampleTitle, currentSampleBody, facts);

        var assistantMessage = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder,
            Role = WritingCoachMessageRole.Assistant,
            Content = string.Empty,
            Status = WritingCoachMessageStatus.Pending,
        };
        await conversations.AddMessageAsync(assistantMessage, cancellationToken);
        await conversations.SaveChangesAsync(cancellationToken);

        var textBuilder = new StringBuilder();
        var streamFailed = false;
        string? streamError = null;
        var cancelled = false;

        var enumerator = chat.GetStreamingResponseAsync(messages, new ChatOptions(), cancellationToken)
                             .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Writing Coach streaming failed");
                    streamFailed = true;
                    streamError = ex.Message;
                    break;
                }

                if (!hasNext) break;

                foreach (var content in enumerator.Current.Contents)
                {
                    if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        textBuilder.Append(textContent.Text);
                        yield return new WritingCoachTextDelta(textContent.Text);
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (cancelled)
        {
            assistantMessage.Content = textBuilder.ToString();
            assistantMessage.Status = WritingCoachMessageStatus.Cancelled;
            assistantMessage.ErrorMessage = "Cancelled by user.";
            await SafePersistAsync(assistantMessage);
            yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
            yield break;
        }

        if (streamFailed)
        {
            assistantMessage.Content = textBuilder.ToString();
            assistantMessage.Status = WritingCoachMessageStatus.Failed;
            assistantMessage.ErrorMessage = streamError;
            await SafePersistAsync(assistantMessage);
            yield return new WritingCoachTurnError(streamError ?? "Writing Coach streaming failed.", Cancelled: false);
            yield break;
        }

        assistantMessage.Content = textBuilder.ToString();
        assistantMessage.Status = WritingCoachMessageStatus.Completed;
        await SafePersistAsync(assistantMessage);
        conversation.UpdatedAt = DateTime.UtcNow;
        await conversations.SaveChangesAsync(CancellationToken.None);
        yield return new WritingCoachAssistantMessageCompleted(assistantMessage.Id);
    }

    private static List<ChatMessage> BuildMessages(
        IReadOnlyList<WritingCoachMessage> history,
        Guid currentUserMessageId,
        string currentUserText,
        string? currentSampleTitle,
        string? currentSampleBody,
        IReadOnlyList<ProjectFact> projectFacts)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(currentSampleTitle, currentSampleBody)),
        };

        if (projectFacts.Count > 0)
            messages.Add(new ChatMessage(ChatRole.System, BuildProjectFactsPrompt(projectFacts)));

        messages.AddRange(history
            .Where(message => message.Id != currentUserMessageId && message.Status == WritingCoachMessageStatus.Completed)
            .Select(ToChatMessage));

        messages.Add(new ChatMessage(ChatRole.User, currentUserText));
        return messages;
    }

    private static string BuildSystemPrompt(string? currentSampleTitle, string? currentSampleBody)
    {
        var title = string.IsNullOrWhiteSpace(currentSampleTitle) ? "Untitled writing sample" : currentSampleTitle.Trim();
        var body = currentSampleBody ?? string.Empty;
        var visibleBody = body.Length == 0 ? "(empty draft)" : body;

        return $"""
            {CoachSystemPrompt}

            Current writing sample draft, read-only:
            Title: {title}

            Body:
            {visibleBody}
            """;
    }

    private static string BuildProjectFactsPrompt(IReadOnlyList<ProjectFact> projectFacts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Project facts, read-only system context:");
        sb.AppendLine("Use these facts to keep coaching grounded in the project's established direction. Do not edit, create, or delete facts from this chat.");

        foreach (var fact in projectFacts)
        {
            sb.Append("- ").Append(fact.Name);
            if (!string.Equals(fact.Name, fact.Key, StringComparison.OrdinalIgnoreCase))
                sb.Append(" [").Append(fact.Key).Append(']');

            var value = string.IsNullOrWhiteSpace(fact.Value) ? "(empty)" : fact.Value.Trim();
            sb.Append(": ").AppendLine(value);

            if (fact.LinkedEntities.Count > 0)
            {
                sb.Append("  Linked entities: ");
                sb.AppendLine(string.Join("; ", fact.LinkedEntities.Select(link =>
                    $"{link.EdgeType} {link.EntityName} ({link.EntityType})")));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static ChatMessage ToChatMessage(WritingCoachMessage message) => message.Role switch
    {
        WritingCoachMessageRole.System => new ChatMessage(ChatRole.System, message.Content),
        WritingCoachMessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        WritingCoachMessageRole.Assistant => new ChatMessage(ChatRole.Assistant, message.Content),
        _ => new ChatMessage(ChatRole.User, message.Content),
    };

    private async Task PersistFailedAssistantAsync(Guid conversationId, int order, string error)
    {
        try
        {
            var message = new WritingCoachMessage
            {
                ConversationId = conversationId,
                Order = order,
                Role = WritingCoachMessageRole.Assistant,
                Status = WritingCoachMessageStatus.Failed,
                ErrorMessage = error,
            };
            await conversations.AddMessageAsync(message, CancellationToken.None);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Writing Coach setup failure");
        }
    }

    private async Task SafePersistAsync(WritingCoachMessage message)
    {
        try
        {
            conversations.UpdateMessage(message);
            await conversations.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Writing Coach message {MessageId}", message.Id);
        }
    }
}
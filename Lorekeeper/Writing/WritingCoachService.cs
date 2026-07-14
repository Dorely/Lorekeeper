using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lorekeeper.ChatTurns;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Writing;

public sealed class WritingCoachService(
    IProjectRepository projects,
    IWritingCoachConversationRepository conversations,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    WritingCoachTools tools,
    ChatTurnEngine turnEngine,
    IOptions<AgentOptions> options,
    ILogger<WritingCoachService> logger) : IWritingCoachService
{
    public static readonly string CoachSystemPrompt = """
        You are a Writing Coach for a long-form fiction project. Your job is to help
        the writer produce writing samples in their own style and words so future AI
        drafting can better imitate their voice.

        How to work:
        - You are a partner, not an oracle. Ask questions, propose options, and
          surface craft trade-offs. Do not take over the prose.
        - At the beginning of every user turn, call read_current_section before you
          answer. Treat its result as the latest current draft. Do not ask the user to
          paste the current section unless the tool result says it is unavailable.
        - Call list_project_facts when project-level context would change your advice,
          especially for premise, tone, setting, character, canon, style constraints,
          or other established truths.
        - Your tools are read-only. You cannot edit, save, rename, delete, or otherwise
          change stored samples or project facts from this chat.
        - Do not claim you changed the draft or stored anything.
        - Avoid taking over the prose. When the user asks for examples, keep them short
          and frame them as options the writer can adapt.
        - Keep replies concise and practical. Prefer one next step over a broad lecture.
        - Pay attention to sentence rhythm, diction, point of view, imagery, pacing,
          and emotional texture. Help the writer make those choices intentional.
        """ + "\n\n" + AssistantWorkflowInstructions.NonReplayedToolHistory;

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
        var providerAvailability = await providerService.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
        if (!providerAvailability.IsAvailable || providerAvailability.Provider is null)
        {
            yield return new WritingCoachTurnError(providerAvailability.Message, Cancelled: false);
            yield break;
        }

        var nextOrder = await conversations.GetMaxOrderAsync(conversation.Id, cancellationToken) + 1;

        var userMessage = new WritingCoachMessage
        {
            ConversationId = conversation.Id,
            Order = nextOrder++,
            Role = WritingCoachMessageRole.User,
            Content = userText.Trim(),
            Status = WritingCoachMessageStatus.Completed,
        };
        conversation.UpdatedAt = DateTime.UtcNow;
        await turnEngine.AddMessageAsync(conversations, userMessage, cancellationToken);

        IChatClient chat = null!;
        IList<AITool> aiTools = null!;
        string? setupError = null;
        try
        {
            var project = await projects.GetByIdAsync(projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
            chat = await chatClientFactory.CreateChatClientAsync(providerAvailability.Provider.Id, cancellationToken);
            aiTools = tools.Build(new WritingCoachContext(project.Id, currentSampleTitle, currentSampleBody));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing Coach turn setup failed for project {ProjectId}", projectId);
            await PersistFailedAssistantAsync(conversation.Id, nextOrder, ex.Message);
            setupError = ex.Message;
        }

        if (setupError is not null)
        {
            yield return new WritingCoachTurnError(setupError, Cancelled: false);
            yield break;
        }

        var chatOptions = new ChatOptions
        {
            Tools = aiTools,
            ToolMode = ChatToolMode.Auto,
        };

        var history = await conversations.LoadMessagesAsync(conversation.Id, cancellationToken);
        var messages = new List<ChatMessage> { new(ChatRole.System, CoachSystemPrompt) };
        messages.AddRange(ChatModelHistory.Build(
            history,
            message => message.Role.ToString(),
            message => message.Content));

        var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var activeAssistant = new WritingCoachMessage
            {
                ConversationId = conversation.Id,
                Order = nextOrder++,
                Role = WritingCoachMessageRole.Assistant,
                Content = string.Empty,
                Status = WritingCoachMessageStatus.Pending,
            };
            await turnEngine.AddMessageAsync(conversations, activeAssistant, cancellationToken);

            ChatRoundCompleted? completedRound = null;
            await foreach (var update in turnEngine.StreamRoundAsync(chat, messages, chatOptions, cancellationToken))
            {
                switch (update)
                {
                    case ChatRoundTextDelta text:
                        yield return new WritingCoachTextDelta(text.Text);
                        break;
                    case ChatRoundToolCallStarted started:
                        yield return new WritingCoachToolCallStarted(started.CallId, started.ToolName, started.ArgumentsJson, started.ArgumentsComplete);
                        break;
                    case ChatRoundToolCallArgumentsDelta delta:
                        yield return new WritingCoachToolCallArgumentsDelta(delta.CallId, delta.ArgumentsDelta, delta.ArgumentsComplete);
                        break;
                    case ChatRoundCompleted completed:
                        completedRound = completed;
                        break;
                    case ChatRoundFailed failed:
                        activeAssistant.Content = failed.Text;
                        activeAssistant.Status = failed.Cancelled
                            ? WritingCoachMessageStatus.Cancelled
                            : WritingCoachMessageStatus.Failed;
                        activeAssistant.ErrorMessage = failed.Cancelled ? "Cancelled by user." : failed.Message;
                        await SafePersistAsync(activeAssistant);
                        yield return new WritingCoachTurnError(failed.Message, failed.Cancelled);
                        yield break;
                }
            }

            if (completedRound is null)
            {
                activeAssistant.Status = WritingCoachMessageStatus.Failed;
                activeAssistant.ErrorMessage = "Writing Coach streaming ended without a completed round.";
                await SafePersistAsync(activeAssistant);
                yield return new WritingCoachTurnError(activeAssistant.ErrorMessage, Cancelled: false);
                yield break;
            }

            var textBuilder = new StringBuilder(completedRound.Text);
            var pendingCalls = completedRound.ToolCalls;

            if (pendingCalls.Count == 0)
            {
                activeAssistant.Content = textBuilder.ToString();
                activeAssistant.Status = WritingCoachMessageStatus.Completed;
                await SafePersistAsync(activeAssistant);
                conversation.UpdatedAt = DateTime.UtcNow;
                await conversations.SaveChangesAsync(CancellationToken.None);
                yield return new WritingCoachAssistantMessageCompleted(activeAssistant.Id);
                yield break;
            }

            var manifest = pendingCalls
                .Select(pendingCall => new ChatToolCallManifest(
                    pendingCall.CallId,
                    pendingCall.Name,
                    pendingCall.ArgumentsJson,
                    pendingCall.TextOffset))
                .ToList();
            activeAssistant.Content = textBuilder.ToString();
            activeAssistant.ToolCallsJson = JsonSerializer.Serialize(manifest);
            activeAssistant.Status = WritingCoachMessageStatus.Completed;
            await SafePersistAsync(activeAssistant);

            messages.Add(new ChatMessage(ChatRole.Assistant, ChatTurnEngine.BuildAssistantContents(textBuilder.ToString(), manifest)));

            var resultContents = new List<AIContent>();
            foreach (var pendingCall in pendingCalls)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var sw = Stopwatch.StartNew();
                var toolOutcome = await turnEngine.InvokeToolAsync(aiTools, pendingCall, cancellationToken);
                sw.Stop();

                if (toolOutcome.Cancelled)
                {
                    yield return new WritingCoachTurnError("Cancelled.", Cancelled: true);
                    yield break;
                }

                var toolResult = toolOutcome.Result;
                var toolError = toolOutcome.Error;

                var toolMessage = new WritingCoachMessage
                {
                    ConversationId = conversation.Id,
                    Order = nextOrder++,
                    Role = WritingCoachMessageRole.Tool,
                    Content = toolResult ?? string.Empty,
                    ToolCallId = pendingCall.CallId,
                    ToolName = pendingCall.Name,
                    Status = toolError is null ? WritingCoachMessageStatus.Completed : WritingCoachMessageStatus.Failed,
                    ErrorMessage = toolError,
                };
                await turnEngine.AddMessageAsync(conversations, toolMessage, CancellationToken.None);

                resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult ?? string.Empty));
                yield return new WritingCoachToolCallCompleted(
                    pendingCall.CallId,
                    pendingCall.Name,
                    toolError is null ? toolResult : null,
                    toolError,
                    sw.Elapsed.TotalMilliseconds);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

            if (iteration == maxIterations - 1)
            {
                yield return new WritingCoachTurnError(
                    ChatTurnEngine.ToolLoopLimitError(maxIterations),
                    Cancelled: false);
                yield break;
            }
        }
    }

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
            await turnEngine.UpdateMessageAsync(conversations, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist Writing Coach message {MessageId}", message.Id);
        }
    }

}

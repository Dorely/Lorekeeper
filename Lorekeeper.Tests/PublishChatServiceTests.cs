using System.Runtime.CompilerServices;
using Lorekeeper.ChatTurns;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Publish;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Tests;

public sealed class PublishChatServiceTests
{
    [Fact]
    public async Task ConversationIsProjectScopedOrderedPersistentAndResettable()
    {
        var fixture = Fixture.With(new ScriptedChatClient(Complete("Done.")));

        var first = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        var second = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal([0], (await fixture.Service.LoadMessagesAsync(first.Id)).Select(message => message.Order));

        var updates = await CollectAsync(fixture.Service.SendAsync(
            fixture.Project.Id,
            null,
            "Help me publish this.",
            []));
        Assert.Contains(updates, update => update is PublishTextDelta { Text: "Done." });
        Assert.Contains(updates, update => update is PublishAssistantMessageCompleted);
        var persisted = await fixture.Service.LoadMessagesAsync(first.Id);
        Assert.Equal([0, 1, 2], persisted.Select(message => message.Order));
        Assert.Equal(PublishMessageRole.User, persisted[1].Role);
        Assert.Equal(PublishMessageRole.Assistant, persisted[2].Role);
        Assert.Equal(PublishMessageStatus.Completed, persisted[2].Status);

        await fixture.Service.ResetAsync(fixture.Project.Id);
        var reset = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        Assert.NotEqual(first.Id, reset.Id);
        Assert.Single(await fixture.Service.LoadMessagesAsync(reset.Id));
        Assert.Equal(1, fixture.Images.ClearCount);
    }

    [Fact]
    public async Task StreamingFailureAndCancellationPersistHonestTerminalStates()
    {
        var failed = Fixture.With(new ScriptedChatClient(Fail("provider rejected request")));
        var failedUpdates = await CollectAsync(failed.Service.SendAsync(
            failed.Project.Id,
            null,
            "Try this.",
            []));
        var failure = Assert.IsType<PublishTurnError>(failedUpdates[^1]);
        Assert.False(failure.Cancelled);
        Assert.Contains("provider rejected request", failure.Message);
        Assert.Contains(
            await failed.Service.LoadMessagesAsync((await failed.Service.GetOrCreateAsync(failed.Project.Id)).Id),
            message => message.Status == PublishMessageStatus.Failed);

        var cancelled = Fixture.With(new ScriptedChatClient(WaitForCancellation()));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelledUpdates = await CollectAsync(cancelled.Service.SendAsync(
            cancelled.Project.Id,
            null,
            "Keep working.",
            [],
            cancellation.Token));
        var cancellationUpdate = Assert.IsType<PublishTurnError>(cancelledUpdates[^1]);
        Assert.True(cancellationUpdate.Cancelled);
        Assert.Contains(
            await cancelled.Service.LoadMessagesAsync((await cancelled.Service.GetOrCreateAsync(cancelled.Project.Id)).Id),
            message => message.Status == PublishMessageStatus.Cancelled);
    }

    [Fact]
    public async Task SelectedEditionShapesPromptAndToolPayloadsAreNotReplayed()
    {
        var client = new ScriptedChatClient(Complete("Ready."));
        var fixture = Fixture.With(client);
        var editionId = Guid.NewGuid();
        var conversation = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        await fixture.Repository.AddMessageAsync(new PublishMessage
        {
            ConversationId = conversation.Id,
            Order = 1,
            Role = PublishMessageRole.Tool,
            ToolCallId = "prior-tool",
            ToolName = "read_publication_edition",
            Content = "SECRET_TOOL_PAYLOAD",
        });
        await fixture.Repository.SaveChangesAsync();

        var prompt = await fixture.Service.GetSystemPromptAsync(fixture.Project.Id, editionId);
        Assert.Contains("## Project Guidance", prompt);
        Assert.Contains("Project Guidance fixture", prompt);
        Assert.Contains("## Book Brief", prompt);
        Assert.DoesNotContain("Current Chapter", prompt, StringComparison.OrdinalIgnoreCase);

        await CollectAsync(fixture.Service.SendAsync(
            fixture.Project.Id,
            editionId,
            "Continue.",
            []));

        Assert.Equal(ContextBuildPurpose.Publish, fixture.Context.LastRequest?.Purpose);
        Assert.Contains(editionId.ToString("D"), fixture.Context.LastRequest?.OperatingRules);
        Assert.Contains("Project Guidance", fixture.Context.LastRequest?.OperatingRules ?? string.Empty);
        Assert.DoesNotContain(
            client.LastMessages ?? [],
            message => message.Role == ChatRole.Tool
                || message.Text.Contains("SECRET_TOOL_PAYLOAD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulToolRoundStreamsActivityPersistsResultAndEmitsMutationNotice()
    {
        var targetEditionId = Guid.NewGuid();
        var tool = AIFunctionFactory.Create(
            method: (Guid editionId) => """{"revision":8}""",
            name: "patch_publication_release_overrides");
        var client = new ScriptedChatClient(
            ToolCall("call-1", "patch_publication_release_overrides", targetEditionId),
            Complete("The release was updated."));
        var fixture = Fixture.With(client, new ToolCatalogStub([tool]));

        var updates = await CollectAsync(fixture.Service.SendAsync(
            fixture.Project.Id,
            targetEditionId,
            "Set the explicit publishing change.",
            []));

        Assert.Contains(updates, update => update is PublishToolCallStarted
        {
            CallId: "call-1",
            ToolName: "patch_publication_release_overrides",
        });
        var completed = Assert.Single(updates.OfType<PublishToolCallCompleted>());
        Assert.Null(completed.Error);
        Assert.Contains("revision", completed.Result);
        Assert.Contains(updates, update => update == new PublishWorkspaceMutated(targetEditionId, SelectEdition: false));
        Assert.IsType<PublishAssistantMessageCompleted>(updates[^1]);

        var conversation = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        var messages = await fixture.Service.LoadMessagesAsync(conversation.Id);
        var toolMessage = Assert.Single(messages, message => message.Role == PublishMessageRole.Tool);
        Assert.Equal("call-1", toolMessage.ToolCallId);
        Assert.Equal(PublishMessageStatus.Completed, toolMessage.Status);
    }

    [Fact]
    public async Task ExploratoryTurnCanRemainCollaborativeWithoutInventingAMutation()
    {
        var fixture = Fixture.With(new ScriptedChatClient(
            Complete("Which distributor and finished trim size are you targeting?")));

        var updates = await CollectAsync(fixture.Service.SendAsync(
            fixture.Project.Id,
            null,
            "Help me decide how to publish this book.",
            []));

        Assert.Contains(updates, update => update is PublishTextDelta
        {
            Text: "Which distributor and finished trim size are you targeting?",
        });
        Assert.DoesNotContain(updates, update => update is PublishWorkspaceMutated);
        var conversation = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        Assert.DoesNotContain(
            await fixture.Service.LoadMessagesAsync(conversation.Id),
            message => message.Role == PublishMessageRole.Tool);
    }

    [Fact]
    public void PromptAndMutationNoticesEnforceCollaborativeRevisionAwareBehavior()
    {
        Assert.Contains("Execute explicit instructions directly", PublishChatService.WorkflowInstructions);
        Assert.Contains("Ask only for a genuinely material unknown", PublishChatService.WorkflowInstructions);
        Assert.Contains("After a conflict", PublishChatService.WorkflowInstructions);
        Assert.Contains("reread", PublishChatService.WorkflowInstructions);
        Assert.Contains("cannot approve", PublishChatService.WorkflowInstructions);
        Assert.Contains("application-managed", PublishChatService.WorkflowInstructions);
        Assert.Contains("vendor acceptance", PublishChatService.WorkflowInstructions);
        Assert.Contains("Title, copyright, and visible contents pages are generated", PublishChatService.WorkflowInstructions);
        Assert.Contains("preview_publication_cover_canvas", PublishChatService.WorkflowInstructions);
        Assert.Contains("clean mode before reporting completion", PublishChatService.WorkflowInstructions);

        var editionId = Guid.NewGuid();
        var created = PublishChatService.TryMutationNotice(
            "create_publication_release",
            "{}",
            $$"""{"id":"{{editionId}}"}""");
        Assert.Equal(new PublishWorkspaceMutated(editionId, SelectEdition: true), created);

        var updated = PublishChatService.TryMutationNotice(
            "patch_publication_release_overrides",
            $$"""{"releaseId":"{{editionId}}"}""",
            "{}");
        Assert.Equal(new PublishWorkspaceMutated(editionId, SelectEdition: false), updated);
        Assert.Equal(
            PublishWorkspaceMutationKind.Package,
            PublishChatService.TryMutationNotice(
                "prepare_publication_files",
                $$"""{"releaseId":"{{editionId}}"}""",
                "{}")?.Kind);
        Assert.Equal(
            new PublishWorkspaceMutated(null, SelectEdition: false, PublishWorkspaceMutationKind.Package),
            PublishChatService.TryMutationNotice(
                "prepare_publication_files",
                """{"releaseId":null}""",
                "{}"));
        Assert.NotNull(PublishChatService.TryMutationNotice("patch_publication_book", "{}", "{}"));
        Assert.NotNull(PublishChatService.TryMutationNotice("patch_publication_book_page_setup", "{}", "{}"));
        Assert.NotNull(PublishChatService.TryMutationNotice("upsert_publication_book_text_style", "{}", "{}"));
        Assert.NotNull(PublishChatService.TryMutationNotice("delete_publication_book_text_style", "{}", "{}"));
        Assert.Null(PublishChatService.TryMutationNotice("read_publication_release", "{}", "{}"));
    }

    [Fact]
    public async Task PublishTurnRuntimeReplaysBufferedActivityToAReconnectedSubscriber()
    {
        var runtime = new ChatTurnRuntime();
        var projectId = Guid.NewGuid();
        var key = new ChatTurnKey(projectId, ChatTurnSurface.Publish);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(runtime.TryStart(
            key,
            "Generate the PDFs.",
            Run,
            static (exception, cancelled) => new PublishTurnError(exception.Message, cancelled),
            static update => update is PublishAssistantMessageCompleted or PublishTurnError));
        await buffered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshot = runtime.GetActiveTurn(key);
        Assert.NotNull(snapshot);
        Assert.Equal("Generate the PDFs.", snapshot.UserText);
        await using var subscription = Assert.IsAssignableFrom<IChatTurnSubscription<PublishTurnUpdate>>(
            runtime.Subscribe<PublishTurnUpdate>(key));
        var enumerator = subscription.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(new PublishTextDelta("buffered"), enumerator.Current);
        finish.SetResult();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<PublishAssistantMessageCompleted>(enumerator.Current);
        await enumerator.DisposeAsync();

        async IAsyncEnumerable<PublishTurnUpdate> Run(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new PublishTextDelta("buffered");
            buffered.SetResult();
            await finish.Task.WaitAsync(cancellationToken);
            yield return new PublishAssistantMessageCompleted(Guid.NewGuid());
        }
    }

    [Fact]
    public async Task ResetCannotDeleteConversationOwnedByAnotherActiveCircuit()
    {
        var fixture = Fixture.With(new ScriptedChatClient(Complete("Done.")));
        var conversation = await fixture.Service.GetOrCreateAsync(fixture.Project.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = new ChatTurnKey(fixture.Project.Id, ChatTurnSurface.Publish);
        Assert.True(fixture.Runtime.TryStart(
            key,
            "Still working",
            Run,
            static (exception, cancelled) => new PublishTurnError(exception.Message, cancelled),
            static update => update is PublishAssistantMessageCompleted or PublishTurnError));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.ResetAsync(fixture.Project.Id));
        Assert.Contains("another window", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(conversation.Id, (await fixture.Service.GetOrCreateAsync(fixture.Project.Id)).Id);

        finish.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (fixture.Runtime.GetActiveTurn(key) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Null(fixture.Runtime.GetActiveTurn(key));

        async IAsyncEnumerable<PublishTurnUpdate> Run(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            entered.SetResult();
            await finish.Task.WaitAsync(cancellationToken);
            yield return new PublishAssistantMessageCompleted(Guid.NewGuid());
        }
    }

    private static async Task<List<PublishTurnUpdate>> CollectAsync(
        IAsyncEnumerable<PublishTurnUpdate> updates)
    {
        var result = new List<PublishTurnUpdate>();
        await foreach (var update in updates)
            result.Add(update);
        return result;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Complete(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate { Contents = [new TextContent(text)] };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCall(
        string callId,
        string toolName,
        Guid editionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate
        {
            Contents =
            [
                new FunctionCallContent(
                    callId,
                    toolName,
                    new Dictionary<string, object?> { ["editionId"] = editionId }),
            ],
        };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Fail(
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(message);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> WaitForCancellation(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private sealed class Fixture
    {
        private Fixture(ScriptedChatClient client, IPublishAssistantTools? tools)
        {
            Project = new Project
            {
                Name = "Publish fixture",
                Slug = $"publish-{Guid.NewGuid():N}",
                ProjectGuidance = "Project Guidance fixture",
            };
            Repository = new InMemoryConversationRepository();
            Images = new ImageAttachmentsStub();
            Context = new ContextBuilderStub();
            Runtime = new ChatTurnRuntime();
            Service = new PublishChatService(
                new ProjectRepositoryStub(Project),
                Repository,
                Images,
                new ProviderServiceStub(),
                new ChatClientFactoryStub(client),
                Context,
                tools ?? new ToolCatalogStub(),
                new PublicationActorContext(),
                Runtime,
                new ChatTurnEngine(
                    NullLogger<ChatTurnEngine>.Instance,
                    new NoopChatContextCompactionService()),
                Options.Create(new AgentOptions { MaxToolIterations = 4 }),
                NullLogger<PublishChatService>.Instance);
        }

        public Project Project { get; }
        public InMemoryConversationRepository Repository { get; }
        public ImageAttachmentsStub Images { get; }
        public ContextBuilderStub Context { get; }
        public ChatTurnRuntime Runtime { get; }
        public PublishChatService Service { get; }
        public static Fixture With(ScriptedChatClient client, IPublishAssistantTools? tools = null) => new(client, tools);
    }

    private sealed class NoopChatContextCompactionService : IChatContextCompactionService
    {
        public ChatCompactionResult? TryCompact(IList<ChatMessage> messages, string? modelId) => null;
    }

    private sealed class InMemoryConversationRepository : IPublishConversationRepository
    {
        private readonly List<PublishConversation> _conversations = [];
        private readonly List<PublishMessage> _messages = [];

        public Task<PublishConversation?> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_conversations.SingleOrDefault(conversation => conversation.ProjectId == projectId));
        public Task<List<PublishMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_messages.Where(message => message.ConversationId == conversationId).OrderBy(message => message.Order).ToList());
        public Task<int> GetMaxOrderAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_messages.Where(message => message.ConversationId == conversationId).Select(message => message.Order).DefaultIfEmpty(-1).Max());
        public Task AddConversationAsync(PublishConversation conversation, CancellationToken cancellationToken = default)
        {
            _conversations.Add(conversation);
            return Task.CompletedTask;
        }
        public Task AddMessageAsync(PublishMessage message, CancellationToken cancellationToken = default)
        {
            _messages.Add(message);
            return Task.CompletedTask;
        }
        public void UpdateMessage(PublishMessage message) { }
        public void RemoveConversation(PublishConversation conversation)
        {
            _conversations.Remove(conversation);
            _messages.RemoveAll(message => message.ConversationId == conversation.Id);
        }
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ProjectRepositoryStub(Project project) : IProjectRepository
    {
        public Task<List<Project>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<Project> { project });
        public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(id == project.Id ? project : null);
        public Task<Project?> GetSnapshotByIdAsync(Guid id, CancellationToken cancellationToken = default) => GetByIdAsync(id, cancellationToken);
        public Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult<Project?>(slug == project.Slug ? project : null);
        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(slug == project.Slug);
        public Task AddAsync(Project value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Update(Project value) => throw new NotSupportedException();
        public void Remove(Project value) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ContextBuilderStub : IContextBuilder
    {
        public ContextBuildRequest? LastRequest { get; private set; }
        public Task<ContextAssembly> BuildAsync(ContextBuildRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ContextAssembly([
                new ContextItem("workflow", ContextItemKind.AssistantWorkflow, "Assistant workflow", request.OperatingRules ?? string.Empty, true, false),
                new ContextItem("guidance", ContextItemKind.ProjectGuidance, "Project Guidance", request.Project.ProjectGuidance, true, false),
                new ContextItem("brief", ContextItemKind.BookBrief, "Book Brief", "Fixture brief", true, false),
            ]));
        }
    }

    private sealed class ToolCatalogStub(IList<AITool>? tools = null) : IPublishAssistantTools
    {
        public Task<IList<AITool>> BuildAsync(PublishAssistantContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(tools ?? []);
    }

    private sealed class ScriptedChatClient(params IAsyncEnumerable<ChatResponseUpdate>[] rounds) : IChatClient
    {
        private int _round;
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            var updates = rounds[Math.Min(_round++, rounds.Length - 1)];
            await foreach (var update in updates.WithCancellation(cancellationToken))
                yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ChatClientFactoryStub(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateChatClientAsync(int providerId, CancellationToken cancellationToken = default) => Task.FromResult(client);
        public Task TestModelAsync(int providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TestModelAsync(LlmProvider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ImageAttachmentsStub : IChatImageAttachmentService
    {
        public int ClearCount { get; private set; }
        public Task<IReadOnlyList<ChatTurnImageAttachment>> ResolveAsync(Guid projectId, IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatTurnImageAttachment>>([]);
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<ChatTurnImageAttachment>>> LoadForSurfaceAsync(Guid projectId, ChatTurnSurface surface, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PersistAsync(Guid projectId, ChatTurnSurface surface, Guid messageId, IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ChatMessage> BuildUserMessageAsync(Guid projectId, string userText, IReadOnlyList<Guid> imageIds, string? guidance = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearSurfaceAsync(Guid projectId, ChatTurnSurface surface, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ChatTurnSurface.Publish, surface);
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ProviderServiceStub : ILlmProviderService
    {
        private readonly LlmProvider _provider = new() { Id = 1, Name = "fixture", EndpointUrl = "https://example.invalid", ModelId = "fixture" };
        public Task<ChatProviderAvailability> GetDefaultChatProviderAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(ChatProviderAvailability.Available(_provider));
        public Task<bool> IsVisionProviderWorkingAsync(int providerId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<List<LlmProvider>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider?> GetByNameAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider?> GetDefaultAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VisionProviderAvailability> GetDefaultVisionProviderAvailabilityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<LlmProvider>> ListWorkingChatProvidersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<LlmProvider>> ListWorkingVisionProvidersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsChatProviderWorkingAsync(int providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsCodexConnectedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider> CreateAsync(LlmProvider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider> UpdateAsync(LlmProvider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateConnectionAsync(LlmConnectionUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteConnectionAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetDefaultAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider> MarkChatTestSucceededAsync(LlmProvider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider> MarkChatTestFailedAsync(LlmProvider provider, string error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider> MarkVisionTestSucceededAsync(LlmProvider provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmProvider> MarkVisionTestFailedAsync(LlmProvider provider, string error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetEffectiveApiKeyAsync(int providerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

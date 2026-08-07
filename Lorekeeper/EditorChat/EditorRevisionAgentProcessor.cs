using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Ingest;
using Lorekeeper.Llm;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.EditorChat;

public sealed class EditorRevisionAgentProcessor(
    IProjectRepository projects,
    IChapterService chapters,
    IManuscriptService manuscripts,
    IEditorConversationRepository conversations,
    IEditorRevisionRepository revisions,
    IContextBuilder contextBuilder,
    IEntityVisualContextService entityVisualContext,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    IProjectSearchService projectSearch,
    IAiChangeRepository changes,
    IProjectFactService projectFacts,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IEntityRelationContextService entityRelations,
    IEntityVisualExampleService entityVisualExamples,
    IOptions<EditorChatOptions> options,
    IEditorRevisionJobNotifier notifier,
    ILogger<EditorRevisionAgentProcessor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    public async Task RunSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await revisions.GetSessionAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException($"Revision session {sessionId} not found.");
        var job = session.Job;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var project = await projects.GetByIdAsync(job.ProjectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {job.ProjectId} not found.");
            var chapter = await chapters.GetAsync(session.ChapterId, cancellationToken)
                ?? throw new InvalidOperationException($"Chapter {session.ChapterId} not found.");
            if (chapter.ProjectId != job.ProjectId)
                throw new InvalidOperationException($"Chapter {session.ChapterId} does not belong to project {job.ProjectId}.");

            var providerAvailability = await providerService.GetDefaultChatProviderAvailabilityAsync(cancellationToken);
            var provider = providerAvailability.Provider;
            if (!providerAvailability.IsAvailable || provider is null)
                throw new InvalidOperationException(providerAvailability.Message);

            var chat = await chatClientFactory.CreateChatClientAsync(provider.Id, cancellationToken);

            session.Status = EditorRevisionSessionStatus.Running;
            session.ProviderId = provider.Id;
            session.ProviderName = provider.DisplayName ?? provider.Name;
            session.ModelName = provider.ModelId;
            session.UpdatedAt = DateTime.UtcNow;
            revisions.UpdateSession(session);
            await revisions.SaveChangesAsync(cancellationToken);
            NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.Progress);

            var userPrompt = await BuildWorkerUserPromptAsync(job, session, cancellationToken);
            var contextAssembly = await contextBuilder.BuildAsync(
                new ContextBuildRequest(project, chapter, userPrompt, ContextBuildPurpose.EditorRevision),
                cancellationToken);
            var systemPrompt = contextAssembly.Assemble();
            var nextOrder = await revisions.GetMaxMessageOrderAsync(session.Id, cancellationToken) + 1;
            await revisions.AddMessageAsync(new EditorRevisionMessage
            {
                SessionId = session.Id,
                Order = nextOrder++,
                Role = EditorRevisionMessageRole.System,
                Content = systemPrompt,
                Status = EditorRevisionMessageStatus.Completed,
            }, cancellationToken);
            await revisions.AddMessageAsync(new EditorRevisionMessage
            {
                SessionId = session.Id,
                Order = nextOrder++,
                Role = EditorRevisionMessageRole.User,
                Content = userPrompt,
                Status = EditorRevisionMessageStatus.Completed,
            }, cancellationToken);
            await revisions.SaveChangesAsync(cancellationToken);
            NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.Progress);

            var edit = new CapturedChapterEdit();
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt),
            };
            if (await entityVisualContext.BuildVisionMessageAsync(
                job.ProjectId,
                contextAssembly.Visuals,
                await providerService.IsVisionProviderWorkingAsync(provider.Id, cancellationToken),
                "Canonical entity visual references for this revision assignment follow. Preserve the established appearance continuity they show.",
                cancellationToken) is { } visualMessage)
            {
                messages.Add(visualMessage);
            }
            var tools = await BuildToolsAsync(job.ProjectId, job.ConversationId, session.ChapterId, edit, cancellationToken);
            var chatOptions = new ChatOptions
            {
                Tools = tools,
                ToolMode = ChatToolMode.Auto,
            };

            var maxIterations = Math.Clamp(options.Value.RevisionAgents.MaxToolIterations, 1, 50);
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var assistant = new EditorRevisionMessage
                {
                    SessionId = session.Id,
                    Order = nextOrder++,
                    Role = EditorRevisionMessageRole.Assistant,
                    Content = string.Empty,
                    Status = EditorRevisionMessageStatus.Pending,
                };
                await revisions.AddMessageAsync(assistant, cancellationToken);
                await revisions.SaveChangesAsync(cancellationToken);

                var textBuilder = new StringBuilder();
                var pendingCalls = new List<PendingToolCall>();
                var toolTracker = new StreamingToolCallTracker();
                await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
                {
                    foreach (var content in update.Contents)
                    {
                        if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            textBuilder.Append(textContent.Text);
                            continue;
                        }

                        foreach (var toolUpdate in toolTracker.Process(content, textBuilder.Length))
                        {
                            if (toolUpdate is StreamingToolCallReadyUpdate ready)
                            {
                                pendingCalls.Add(new PendingToolCall(
                                    ready.Content,
                                    ready.CallId,
                                    ready.ToolName,
                                    ready.ArgumentsJson,
                                    ready.TextOffset));
                            }
                        }
                    }
                }

                assistant.Content = textBuilder.ToString();
                assistant.ToolCallsJson = JsonSerializer.Serialize(
                    pendingCalls.Select(call => new PersistedToolCall(call.CallId, call.Name, call.ArgumentsJson, call.TextOffset)),
                    JsonSerializerOptions.Default);
                assistant.Status = EditorRevisionMessageStatus.Completed;
                revisions.UpdateMessage(assistant);
                await revisions.SaveChangesAsync(cancellationToken);
                NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.Progress);

                if (pendingCalls.Count == 0)
                {
                    MarkInvalid(session, "Worker finished without editing the assigned chapter.", textBuilder.ToString(), stopwatch);
                    revisions.UpdateSession(session);
                    await revisions.SaveChangesAsync(cancellationToken);
                    NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.SessionCompleted);
                    return;
                }

                messages.Add(new ChatMessage(ChatRole.Assistant, BuildAssistantToolCallContents(pendingCalls)));
                var resultContents = new List<AIContent>();
                foreach (var pendingCall in pendingCalls)
                {
                    var toolResult = string.Empty;
                    string? toolError = null;
                    try
                    {
                        var function = tools.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == pendingCall.Name)
                            ?? throw new InvalidOperationException($"Unknown tool '{pendingCall.Name}'.");
                        var invokeResult = await function.InvokeAsync(
                            ToolCallArguments.Create(pendingCall.Content.Arguments, pendingCall.ArgumentsJson),
                            cancellationToken);
                        toolResult = invokeResult?.ToString() ?? string.Empty;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Revision worker tool '{Tool}' failed for session {SessionId}", pendingCall.Name, session.Id);
                        toolError = ex.Message;
                        toolResult = $"Error: {ex.Message}";
                    }

                    if (string.Equals(pendingCall.Name, "apply_assigned_manuscript_operations", StringComparison.Ordinal) && toolError is null)
                        toolResult = await ApplyCapturedEditAsync(project, session, edit, pendingCall, stopwatch, cancellationToken);

                    await revisions.AddMessageAsync(new EditorRevisionMessage
                    {
                        SessionId = session.Id,
                        Order = nextOrder++,
                        Role = EditorRevisionMessageRole.Tool,
                        Content = toolResult,
                        ToolCallId = pendingCall.CallId,
                        ToolName = pendingCall.Name,
                        Status = toolError is null ? EditorRevisionMessageStatus.Completed : EditorRevisionMessageStatus.Failed,
                        ErrorMessage = toolError,
                    }, cancellationToken);
                    await revisions.SaveChangesAsync(cancellationToken);
                    NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.Progress);

                    resultContents.Add(new FunctionResultContent(pendingCall.CallId, toolResult));
                    if (string.Equals(pendingCall.Name, "apply_assigned_manuscript_operations", StringComparison.Ordinal))
                    {
                        if (toolError is not null)
                        {
                            MarkInvalid(session, toolError, JsonSerializer.Serialize(edit, JsonOptions), stopwatch);
                            revisions.UpdateSession(session);
                            await revisions.SaveChangesAsync(cancellationToken);
                        }
                        NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.SessionCompleted);
                        return;
                    }
                }

                messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
            }

            MarkInvalid(session, $"Worker exceeded {maxIterations} tool iterations without editing the assigned chapter.", string.Empty, stopwatch);
            revisions.UpdateSession(session);
            await revisions.SaveChangesAsync(cancellationToken);
            NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.SessionCompleted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.Status = EditorRevisionSessionStatus.Cancelled;
            session.ErrorMessage = "Cancelled.";
            session.CompletedAt = DateTime.UtcNow;
            session.UpdatedAt = DateTime.UtcNow;
            revisions.UpdateSession(session);
            await revisions.SaveChangesAsync(CancellationToken.None);
            NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Revision worker session {SessionId} failed", session.Id);
            session.Status = EditorRevisionSessionStatus.Failed;
            session.ErrorMessage = ex.Message;
            session.CompletedAt = DateTime.UtcNow;
            session.UpdatedAt = DateTime.UtcNow;
            session.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            revisions.UpdateSession(session);
            await revisions.SaveChangesAsync(CancellationToken.None);
            NotifyJob(job, session.Id, EditorRevisionJobUpdateKind.SessionCompleted);
        }
    }

    private void NotifyJob(EditorRevisionJob job, Guid? sessionId, EditorRevisionJobUpdateKind kind) =>
        notifier.Notify(new EditorRevisionJobUpdate(
            job.ProjectId,
            job.ConversationId,
            job.ToolCallId,
            job.Id,
            sessionId,
            kind,
            DateTime.UtcNow));

    private async Task<IList<AITool>> BuildToolsAsync(
        Guid projectId,
        Guid parentConversationId,
        Guid assignedChapterId,
        CapturedChapterEdit edit,
        CancellationToken cancellationToken)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(projectId, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Return compact source discovery with complete IDs, total/returned counts, completeness, and exact read_project_source arguments."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null) =>
                    ReadProjectSourceAsync(projectId, sourceType, sourceId, pageNumber),
                name: "read_project_source",
                description: "Read one paginated project source by sourceType and sourceId. Use this before searching only inside a specific source text."),

            AIFunctionFactory.Create(
                method: (
                    string query,
                    int topK = 8,
                    string[]? sourceTypes = null,
                    string[]? sourceIds = null,
                    Guid? containerSourceId = null,
                    bool lexicalOnly = false) =>
                    SearchProjectAsync(projectId, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Hybrid keyword + semantic compact discovery with full IDs, total/returned counts, labeled previews, and exact read_project_source arguments. Use filters and lexicalOnly for source-scoped exact lookup."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(projectId),
                name: "list_chapters",
                description: "List project chapters with ids, order, synopsis, line counts, and read_chapter page counts."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? pageNumber = null) => ReadChapterAsync(projectId, chapterId, pageNumber),
                name: "read_chapter",
                description: "Read one paginated page of a chapter with line numbers. Use this only for read-only grounding."),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(projectId),
                name: "list_project_facts",
                description: "Read project facts and linked entities as JSON."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 10, string? type = null, string? parentId = null) =>
                    SearchEntitiesAsync(projectId, query, topK, type, parentId),
                name: "search_entities",
                description: "Compact entity discovery with full IDs, total/returned counts, completeness, labeled previews, and exact read_entity arguments."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ReadEntityAsync(projectId, entityId, pageNumber),
                name: "read_entity",
                description: "Read one explicitly paginated graph entity with properties, links, and relation context. Full identity fields and GUIDs repeat on every page; follow nextPageArguments."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ListEntityLinksAsync(projectId, entityId, pageNumber),
                name: "list_entity_links",
                description: "List explicitly paginated graph links adjacent to an entity. Full identity fields repeat on every page; follow nextPageArguments."),

            AIFunctionFactory.Create(
                method: (int? pageNumber = null) => ReadParentEditorHistoryAsync(parentConversationId, pageNumber),
                name: "read_parent_editor_history",
                description: "Read the explicitly paginated parent Editor conversation. Every page reports complete message IDs, roles, order, tool names, character counts, and history-window completeness; follow nextPageArguments."),

            AIFunctionFactory.Create(
                method: (int startBlock = 0, int blockCount = 40) =>
                    ReadAssignedManuscriptAsync(assignedChapterId, startBlock, blockCount),
                name: "read_assigned_manuscript",
                description: "Read at most 40 assigned semantic manuscript blocks with stable IDs, inline marks, style roles, total/hasMore metadata, source hash, and required revision token. The complete assigned snapshot is normally already in the Context Feed; use this only when it is missing, stale, or insufficient."),

            AIFunctionFactory.Create(
                method: (string? query = null, string? blockType = null, string? styleRole = null, int start = 0, int count = 40) =>
                    InspectAssignedManuscriptAsync(assignedChapterId, query, blockType, styleRole, start, count),
                name: "inspect_assigned_manuscript",
                description:
                    "Validate and structurally search the assigned manuscript by optional text, blockType, and semantic styleRole. " +
                    "Returns at most 40 matching stable blocks with pagination metadata and bounded normalization/schema diagnostics."),

            AIFunctionFactory.Create(
                method: (
                    string summary,
                    string rationale,
                    long expectedRevision,
                    ManuscriptOperationInput[] operations,
                    string? notes = null) =>
                    CaptureAssignedManuscriptOperationsAsync(edit, assignedChapterId, summary, rationale, expectedRevision, operations, notes),
                name: "apply_assigned_manuscript_operations",
                description:
                    "Terminal mutating tool. Edit only the assigned chapter through semantic insert, replace, delete, move, split, merge, block-type, block-style, or inline-mark operations. " +
                    "Use stable block IDs and expectedRevision from the assigned-manuscript Context Feed snapshot or a refreshed read_assigned_manuscript result. " +
                    "Semantic styleRole values must be lowercase hyphenated identifiers (for example body or scene-break), and sceneBreak insertions must use an empty text value rather than a visible separator such as ***. " +
                    "Do not call any more tools after this."),
        };

        return tools;
    }

    private async Task<string> BuildWorkerUserPromptAsync(EditorRevisionJob job, EditorRevisionSession session, CancellationToken cancellationToken)
    {
        var history = await conversations.LoadMessagesAsync(job.ConversationId, cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine("# Chapter Revision Assignment");
        sb.AppendLine("You are one prose-only worker. Use the complete assigned manuscript snapshot in the Context Feed, then edit it with apply_assigned_manuscript_operations; do not mutate any other project state. Read the assigned manuscript only if the snapshot is missing, stale, or insufficient.");
        sb.AppendLine();
        sb.AppendLine("Assigned chapter:");
        sb.AppendLine($"- {session.ChapterTitle} (id={session.ChapterId})");
        sb.AppendLine();
        sb.AppendLine("Reason this chapter is affected:");
        sb.AppendLine(session.Reason);
        sb.AppendLine();
        sb.AppendLine("Chapter-specific instructions:");
        sb.AppendLine(session.Instructions);
        sb.AppendLine();
        sb.AppendLine("# Parent Editor Chat Context");
        sb.AppendLine(BuildParentEditorHistoryPage(job.ConversationId, history, pageNumber: 1));
        sb.AppendLine("Use read_parent_editor_history with the returned nextPageArguments when the first page is not the complete history window.");
        sb.AppendLine();

        sb.AppendLine("# Output Requirement");
        sb.AppendLine("Use lowercase hyphenated semantic styleRole values. A sceneBreak insertion must have empty text (not ***); use styleRole scene-break or omit it so the default is used.");
        sb.AppendLine("Call apply_assigned_manuscript_operations exactly once when ready. The coordinator will review the completed/staged change and decide whether any follow-up action is needed.");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ReadParentEditorHistoryAsync(Guid conversationId, int? pageNumber)
    {
        var history = await conversations.LoadMessagesAsync(conversationId);
        return BuildParentEditorHistoryPage(conversationId, history, pageNumber);
    }

    private static string BuildParentEditorHistoryPage(
        Guid conversationId,
        IReadOnlyList<EditorMessage> history,
        int? pageNumber)
    {
        var messages = history
            .Where(message => message.Role is EditorMessageRole.User or EditorMessageRole.Assistant or EditorMessageRole.Tool)
            .OrderBy(message => message.Order)
            .Select(message => new
            {
                message.Id,
                message.Order,
                role = message.Role.ToString(),
                message.ToolCallId,
                message.ToolName,
                status = message.Status.ToString(),
                contentCharacterCount = message.Content.Length,
                toolCallsCharacterCount = message.ToolCallsJson.Length,
                errorCharacterCount = message.ErrorMessage?.Length ?? 0,
                message.Content,
                message.ToolCallsJson,
                message.ErrorMessage,
                message.CreatedAt,
            })
            .ToList();
        var identity = new JsonObject
        {
            ["conversationId"] = conversationId,
            ["messageCount"] = messages.Count,
            ["historyWindowIsComplete"] = true,
            ["historyWindowStartOrder"] = messages.Count == 0 ? null : messages[0].Order,
            ["historyWindowEndOrder"] = messages.Count == 0 ? null : messages[^1].Order,
        };
        return AgentPayloadPaginator.SerializePage(
            identity,
            JsonSerializer.SerializeToNode(new { messages }),
            "read_parent_editor_history",
            new JsonObject(),
            pageNumber);
    }

    private async Task<string> ListSearchSourcesAsync(Guid projectId, string? query, string[]? sourceTypes, int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(projectId, query, sourceTypes, topK);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ReadProjectSourceAsync(Guid projectId, string sourceType, Guid sourceId, int? pageNumber)
    {
        var result = await projectSearch.ReadSourceAsync(projectId, sourceType, sourceId, pageNumber);
        return result is null
            ? $"Error: source {sourceType}/{sourceId:N} was not found in this project."
            : JsonSerializer.Serialize(result, JsonOptions);
    }

    private async Task<string> SearchProjectAsync(
        Guid projectId,
        string query,
        int topK,
        string[]? sourceTypes,
        string[]? sourceIds,
        Guid? containerSourceId,
        bool lexicalOnly)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        var parsedSourceIds = ParseSourceIds(sourceIds, out var parseError);
        if (parseError is not null) return parseError;

        var results = await projectSearch.SearchAsync(new ProjectSearchRequest(
            projectId,
            query.Trim(),
            Math.Clamp(topK, 1, 30),
            sourceTypes,
            parsedSourceIds,
            containerSourceId,
            lexicalOnly));

        return ProjectSearchAgentPayload.SerializeResults(query.Trim(), results);
    }

    private async Task<string> ListChaptersAsync(Guid projectId)
    {
        var list = await chapters.ListAsync(projectId);
        if (list.Count == 0) return "No chapters in this project.";

        var sb = new StringBuilder();
        foreach (var chapter in list)
        {
            var lineCount = ChapterFormatting.SplitLines(chapter.PlainText).Count;
            sb.Append(chapter.Order + 1).Append(". ").Append(chapter.Title)
              .Append(" - id=").Append(chapter.Id)
              .Append(" - lines=").Append(lineCount)
              .Append(" - bodyChars=").Append(chapter.PlainText.Length)
              .Append(" - readChapterPages=").Append(CountReadChapterPages(chapter.PlainText, EffectiveReadChapterPageMaxChars()));
            if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
                sb.Append(" - ").Append(chapter.Synopsis);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> ReadChapterAsync(Guid projectId, Guid chapterId, int? pageNumber)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != projectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var requestedPageNumber = pageNumber ?? 1;
        if (requestedPageNumber < 1)
            return "Error: pageNumber must be 1 or greater.";

        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var lines = ChapterFormatting.SplitLines(chapter.PlainText);
        if (lines.Count == 0)
        {
            if (requestedPageNumber > 1)
                return "Error: pageNumber 1 is the only page available for an empty chapter.";

            return JsonSerializer.Serialize(new
            {
                chapter = new { id = chapter.Id, chapter.Title, chapter.Synopsis },
                pagination = new { currentPage = 1, pageCount = 1, pageMaxChars },
                content = "(empty)",
            });
        }

        var pages = BuildReadChapterPages(lines, pageMaxChars);
        if (requestedPageNumber > pages.Count)
            return $"Error: pageNumber {requestedPageNumber} is beyond the chapter's {pages.Count} page(s).";

        var selectedPage = pages[requestedPageNumber - 1];
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var content = FormatReadChapterPageContent(selectedPage, lineNumberWidth);
        return JsonSerializer.Serialize(new
        {
            chapter = new { id = chapter.Id, chapter.Title, chapter.Synopsis },
            chapterStats = new { totalChapterLines = lines.Count, totalChapterChars = chapter.PlainText.Length },
            pagination = new
            {
                pageStartLine = selectedPage.PageStartLine,
                pageEndLine = selectedPage.PageEndLine,
                currentPage = requestedPageNumber,
                pageCount = pages.Count,
                pageMaxChars,
                hasPreviousPage = requestedPageNumber > 1,
                hasNextPage = requestedPageNumber < pages.Count,
                previousPageNumber = requestedPageNumber > 1 ? requestedPageNumber - 1 : (int?)null,
                nextPageNumber = requestedPageNumber < pages.Count ? requestedPageNumber + 1 : (int?)null,
            },
            content,
        });
    }

    private async Task<string> ListProjectFactsAsync(Guid projectId)
    {
        var facts = await projectFacts.ListAsync(projectId);
        var payload = new List<object>();
        foreach (var fact in facts)
        {
            payload.Add(new
            {
                fact.Id,
                fact.Key,
                fact.Name,
                fact.Value,
                linkedEntities = fact.LinkedEntities.Select(link => new
                {
                    link.EdgeType,
                    direction = link.Direction.ToString(),
                    link.EntityId,
                    link.EntityName,
                    link.EntityType,
                }),
            });
        }

        return JsonSerializer.Serialize(payload);
    }

    private async Task<string> SearchEntitiesAsync(Guid projectId, string query, int topK, string? type, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var parsedParent))
                return $"Error: parentId '{parentId}' is not a valid Guid.";
            parent = parsedParent;
        }

        var searchTerms = SearchTerms(query);
        var typeNames = await SearchableTypeNamesAsync(projectId, type);
        var matches = new List<(StoryEntity Entity, int Score)>();
        foreach (var typeName in typeNames)
        {
            var list = await entities.ListAsync(projectId, typeName, parent);
            matches.AddRange(list
                .Select(entity => (Entity: entity, Score: SearchScore(entity, query, searchTerms)))
                .Where(match => match.Score > 0));
        }

        var selected = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .Select(match => new
            {
                id = match.Entity.Id,
                type = match.Entity.Type,
                name = match.Entity.Name,
                order = match.Entity.Order,
                parentId = match.Entity.ParentId,
                matchScore = match.Score,
                previewIsComplete = false,
                previewCounts = new
                {
                    summaryCharacters = match.Entity.Summary?.Length ?? 0,
                    aliases = match.Entity.Aliases.Count,
                    wikiSections = match.Entity.WikiSections.Count,
                    canonSources = match.Entity.CanonSources.Count,
                    properties = match.Entity.Properties.Count,
                },
                preview = new
                {
                    summaryText = TruncatePropertyValue(match.Entity.Summary),
                    aliases = match.Entity.Aliases.Take(8).ToArray(),
                    wikiSections = CompactWikiSections(match.Entity.WikiSections),
                    canonSources = CompactCanonSources(match.Entity.CanonSources),
                    properties = match.Entity.Properties,
                },
                detailReadTool = "read_entity",
                detailReadArguments = new { entityId = match.Entity.Id, pageNumber = 1 },
            })
            .Cast<object>();
        return AgentPayloadPaginator.SerializeCompactDiscovery(query.Trim(), topK, matches.Count, selected, "read_entity");
    }

    private async Task<string> ReadEntityAsync(Guid projectId, Guid entityId, int? pageNumber)
    {
        var entity = await entities.GetAsync(projectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var links = await entities.ListLinksAsync(projectId, entityId);
        var manualLinks = links.Where(link => !link.IsAutoLink).Select(LinkPayload).ToList();
        var autoMentionLinks = links.Where(link => link.IsAutoLink).Select(LinkPayload).ToList();
        var relationContext = await entityRelations.BuildForEntityAsync(projectId, entityId, EntityRelationOptions);
        var visualExamples = await entityVisualExamples.ListForEntityAsync(projectId, entityId);
        var detail = JsonSerializer.SerializeToNode(new
        {
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            canonSources = entity.CanonSources,
            canonicalVisualReferences = visualExamples.Select(example => new
            {
                example.Id,
                example.EntityId,
                example.Label,
                example.SortOrder,
                example.Origin,
                image = new
                {
                    example.Image.Id,
                    example.Image.FileName,
                    example.Image.AltText,
                    example.Image.Prompt,
                },
            }),
            manualLinks,
            autoMentionLinks,
            relationContextPreview = RelationContextPreview(entity.Id, EntityRelationOptions, relationContext),
        });
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private static object RelationContextPreview(Guid entityId, EntityRelationContextOptions options, object relationContext) => new
    {
        isComplete = false,
        note = "This traversal is a bounded orientation preview. All adjacent links are included separately; use list_entity_links and read_entity to continue traversal.",
        bounds = new { options.Depth, options.MaxDirectLinks, options.MaxTraversalPaths, options.MaxLinksPerNode },
        detailReadTool = "list_entity_links",
        detailReadArguments = new { entityId, pageNumber = 1 },
        value = relationContext,
    };

    private async Task<string> ListEntityLinksAsync(Guid projectId, Guid entityId, int? pageNumber)
    {
        var entity = await entities.GetAsync(projectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";
        var links = await entities.ListLinksAsync(projectId, entityId);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            JsonSerializer.SerializeToNode(new { links = links.Select(LinkPayload) }),
            "list_entity_links",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private async Task<string> ReadAssignedManuscriptAsync(
        Guid chapterId,
        int startBlock,
        int blockCount)
    {
        startBlock = Math.Max(0, startBlock);
        blockCount = Math.Clamp(blockCount, 1, 40);
        var snapshot = await manuscripts.GetManuscriptAsync(chapterId)
            ?? throw new InvalidOperationException($"Assigned manuscript {chapterId:N} was not found.");
        var blocks = snapshot.Document.Content.Skip(startBlock).Take(blockCount).ToList();
        return JsonSerializer.Serialize(new
        {
            snapshot.ChapterId,
            snapshot.Revision,
            snapshot.SourceHash,
            totalBlocks = snapshot.Document.Content.Count,
            startBlock,
            blocks,
            hasMore = startBlock + blocks.Count < snapshot.Document.Content.Count,
        }, ManuscriptCodec.JsonOptions);
    }

    private async Task<string> InspectAssignedManuscriptAsync(
        Guid chapterId,
        string? query,
        string? blockType,
        string? styleRole,
        int start,
        int count)
    {
        var snapshot = await manuscripts.GetManuscriptAsync(chapterId)
            ?? throw new InvalidOperationException($"Assigned manuscript {chapterId:N} was not found.");
        return JsonSerializer.Serialize(
            new
            {
                snapshot.ChapterId,
                snapshot.Revision,
                inspection = ManuscriptInspection.Inspect(
                    snapshot.Document,
                    query,
                    blockType,
                    styleRole,
                    start,
                    count),
            },
            ManuscriptCodec.JsonOptions);
    }

    private Task<string> CaptureAssignedManuscriptOperationsAsync(
        CapturedChapterEdit edit,
        Guid assignedChapterId,
        string summary,
        string rationale,
        long expectedRevision,
        ManuscriptOperationInput[] operations,
        string? notes)
    {
        edit.ChapterId = assignedChapterId;
        edit.Summary = summary?.Trim() ?? string.Empty;
        edit.Rationale = rationale?.Trim() ?? string.Empty;
        edit.ExpectedRevision = expectedRevision;
        edit.Operations = operations ?? [];
        edit.Notes = notes?.Trim() ?? string.Empty;
        return Task.FromResult("Semantic manuscript operations recorded. The system is applying or staging them now. Do not call any more tools.");
    }

    private async Task<string> ApplyCapturedEditAsync(
        Project project,
        EditorRevisionSession session,
        CapturedChapterEdit edit,
        PendingToolCall pendingCall,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateEdit(session, edit);
        if (validationError is not null)
        {
            MarkInvalid(session, validationError, JsonSerializer.Serialize(edit, JsonOptions), stopwatch);
            revisions.UpdateSession(session);
            await revisions.SaveChangesAsync(cancellationToken);
            return $"Error: {validationError}";
        }

        var chapter = await chapters.GetAsync(session.ChapterId, cancellationToken)
            ?? throw new InvalidOperationException($"Chapter {session.ChapterId} not found.");
        if (chapter.ManuscriptRevision != edit.ExpectedRevision
            || !string.Equals(chapter.ManuscriptJson, session.OriginalManuscriptJson, StringComparison.Ordinal))
        {
            var error = "The assigned manuscript changed after this worker session started. No worker edit was applied.";
            MarkInvalid(session, error, JsonSerializer.Serialize(edit, JsonOptions), stopwatch);
            revisions.UpdateSession(session);
            await revisions.SaveChangesAsync(cancellationToken);
            return $"Error: {error}";
        }

        var source = ManuscriptCodec.Deserialize(session.OriginalManuscriptJson);
        var operations = ManuscriptOperationInput.ToOperations(edit.Operations);
        var (proposedDocument, changedBlockIds) = ManuscriptOperations.Apply(source, operations);
        await manuscripts.ValidateDocumentReferencesAsync(
            chapter.Id,
            proposedDocument,
            cancellationToken: cancellationToken);
        var newBody = ManuscriptCodec.ProjectPlainText(proposedDocument);
        var result = BuildEditResult(session.ChapterTitle, session.OriginalPlainText, newBody, edit);

        if (project.AiChangeApprovalEnabled)
        {
            var changeId = await StageChapterBodyEditAsync(
                project, session, edit, pendingCall, proposedDocument, result, cancellationToken);
            edit.Notes = AppendNote(edit.Notes, $"Staged pending change {changeId:N} for review.");
            result = AppendResultLine(result, $"Staged pending change {changeId:N} for review.");
        }
        else
        {
            await manuscripts.ApplyAsync(session.ChapterId, edit.ExpectedRevision, operations, cancellationToken);
            edit.Notes = AppendNote(edit.Notes, $"Applied directly to {changedBlockIds.Count} manuscript block(s).");
            result = AppendResultLine(result, $"Applied directly to {changedBlockIds.Count} manuscript block(s).");
        }

        session.Status = EditorRevisionSessionStatus.Completed;
        session.Summary = edit.Summary;
        session.Rationale = edit.Rationale;
        session.OperationFormat = "semantic_operations";
        session.OperationsJson = JsonSerializer.Serialize(edit.Operations, JsonOptions);
        session.Notes = edit.Notes;
        session.ProposalJson = JsonSerializer.Serialize(edit, JsonOptions);
        session.RawResponse = result;
        session.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        session.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        revisions.UpdateSession(session);
        await revisions.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<Guid> StageChapterBodyEditAsync(
        Project project,
        EditorRevisionSession session,
        CapturedChapterEdit edit,
        PendingToolCall pendingCall,
        ManuscriptDocument proposedDocument,
        string result,
        CancellationToken cancellationToken)
    {
        var batch = new AiChangeBatch
        {
            ProjectId = project.Id,
            ConversationKind = AiChangeConversationKind.Editor,
            ConversationId = session.Job.ConversationId,
            AssistantMessageId = session.Job.AssistantMessageId,
        };
        await changes.AddBatchAsync(batch, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);

        var change = new AiChange
        {
            BatchId = batch.Id,
            Order = 0,
            ToolCallId = session.Job.ToolCallId,
            ToolName = "apply_assigned_manuscript_operations",
            ArgumentsJson = pendingCall.ArgumentsJson,
            Summary = edit.Summary,
            BeforeJson = JsonSerializer.Serialize(new ChapterManuscriptChange(
                session.ChapterId,
                session.ChapterTitle,
                ManuscriptCodec.Deserialize(session.OriginalManuscriptJson).Revision,
                session.OriginalManuscriptJson)),
            AfterJson = JsonSerializer.Serialize(new ChapterManuscriptChange(
                session.ChapterId,
                session.ChapterTitle,
                proposedDocument.Revision,
                ManuscriptCodec.Serialize(proposedDocument))),
            ResultJson = JsonSerializer.Serialize(new { result }),
            ResourceKind = "ChapterManuscript",
            ResourceId = Resource("Chapter", session.ChapterId),
            CreatedResourceIdsJson = "[]",
            ReferencedResourceIdsJson = JsonSerializer.Serialize(new[] { Resource("Chapter", session.ChapterId) }),
            DependsOnChangeIdsJson = "[]",
        };
        await changes.AddChangeAsync(change, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        return change.Id;
    }

    private static string? ValidateEdit(EditorRevisionSession session, CapturedChapterEdit edit)
    {
        if (edit.ChapterId != session.ChapterId)
            return "Edit targeted a chapter other than the assigned chapter.";
        if (string.IsNullOrWhiteSpace(edit.Summary))
            return "Edit summary is required.";
        if (string.IsNullOrWhiteSpace(edit.Rationale))
            return "Edit rationale is required.";
        var original = ManuscriptCodec.Deserialize(session.OriginalManuscriptJson);
        if (edit.ExpectedRevision != original.Revision)
            return $"expectedRevision must be {original.Revision}.";
        if (edit.Operations.Length == 0)
            return "At least one semantic manuscript operation is required.";
        try
        {
            _ = ManuscriptOperations.Apply(original, ManuscriptOperationInput.ToOperations(edit.Operations));
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return exception.Message;
        }
    }

    private static string BuildEditResult(string chapterTitle, string originalBody, string newBody, CapturedChapterEdit edit)
    {
        var beforeBlocks = ManuscriptCodec.FromPlainText(Guid.Empty, originalBody).Content.Count;
        var afterBlocks = ManuscriptCodec.FromPlainText(Guid.Empty, newBody).Content.Count;
        var sb = new StringBuilder();
        sb.Append("Edited ").Append(chapterTitle)
            .Append(": ").Append(edit.Summary)
            .Append(" (").Append(edit.Operations.Length).Append(" semantic operation(s), ")
            .Append(beforeBlocks).Append(" -> ").Append(afterBlocks).AppendLine(" blocks).");
        sb.AppendLine("Rationale:");
        sb.AppendLine(edit.Rationale);
        if (!string.IsNullOrWhiteSpace(edit.Notes))
        {
            sb.AppendLine("Notes:");
            sb.AppendLine(edit.Notes);
        }

        return sb.ToString().TrimEnd();
    }

    private static string AppendNote(string notes, string extra) =>
        string.IsNullOrWhiteSpace(notes) ? extra : $"{notes}\n{extra}";

    private static string AppendResultLine(string result, string extra) =>
        string.IsNullOrWhiteSpace(result) ? extra : $"{result}\n{extra}";

    private static string Resource(string kind, Guid id) => $"{kind}:{id:N}";

    private static void MarkInvalid(EditorRevisionSession session, string error, string rawResponse, Stopwatch stopwatch)
    {
        session.Status = EditorRevisionSessionStatus.Invalid;
        session.ErrorMessage = error;
        session.RawResponse = rawResponse;
        session.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        session.CompletedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
    }

    private int EffectiveReadChapterPageMaxChars() => Math.Max(256, options.Value.ReadChapterPageMaxChars);

    private static int CountReadChapterPages(string body, int pageMaxChars)
    {
        var lines = ChapterFormatting.SplitLines(body);
        return lines.Count == 0 ? 1 : BuildReadChapterPages(lines, pageMaxChars).Count;
    }

    private static List<ReadChapterPage> BuildReadChapterPages(IReadOnlyList<string> lines, int pageMaxChars)
    {
        var pages = new List<ReadChapterPage>();
        var currentPage = new ReadChapterPage();
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var prefixLength = lineNumberWidth + 2;

        for (var lineNumber = 1; lineNumber <= lines.Count; lineNumber++)
        {
            var text = lines[lineNumber - 1];
            var fullLineLength = prefixLength + text.Length;
            var addLength = fullLineLength + (currentPage.Segments.Count == 0 ? 0 : 1);
            if (currentPage.Segments.Count > 0 && currentPage.ContentCharCount + addLength > pageMaxChars)
            {
                pages.Add(currentPage);
                currentPage = new ReadChapterPage();
            }

            currentPage.Add(new ReadChapterLineSegment(lineNumber, text), lineNumberWidth);
        }

        if (currentPage.Segments.Count > 0)
            pages.Add(currentPage);

        return pages;
    }

    private static string FormatReadChapterPageContent(ReadChapterPage page, int lineNumberWidth)
    {
        var sb = new StringBuilder(page.ContentCharCount);
        for (var i = 0; i < page.Segments.Count; i++)
        {
            var segment = page.Segments[i];
            sb.Append(segment.LineNumber.ToString().PadLeft(lineNumberWidth, '0'));
            sb.Append(": ");
            sb.Append(segment.Text);
            if (i < page.Segments.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    private async Task<IReadOnlyList<string>> SearchableTypeNamesAsync(Guid projectId, string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
            return [type.Trim()];

        var list = await entityTypes.ListAsync(projectId, includeStructural: true);
        return list
            .Where(typeDefinition => IsSearchableEntityType(typeDefinition.Type))
            .Select(typeDefinition => typeDefinition.Type)
            .ToList();
    }

    private static string[] SearchTerms(string query) =>
        query.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim('"', '\'', '`', '(', ')', '[', ']', '{', '}', '.', ':'))
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int SearchScore(StoryEntity entity, string query, IReadOnlyList<string> searchTerms)
    {
        var score = TextMatchScore(entity.Name, query, titleWeight: 80, detailWeight: 30);
        score += TextMatchScore(entity.Type, query, titleWeight: 12, detailWeight: 8);
        score += TextMatchScore(entity.Summary, query, titleWeight: 20, detailWeight: 12);
        foreach (var alias in entity.Aliases)
            score += TextMatchScore(alias, query, titleWeight: 30, detailWeight: 16);
        foreach (var section in entity.WikiSections)
        {
            score += TextMatchScore(section.Title, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(section.Body, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var canonSource in entity.CanonSources)
        {
            score += TextMatchScore(canonSource.SourceTitle, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(canonSource.Markdown, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var property in entity.Properties)
        {
            score += TextMatchScore(property.Key, query, titleWeight: 8, detailWeight: 4);
            score += TextMatchScore(property.Value, query, titleWeight: 8, detailWeight: 4);
        }

        foreach (var term in searchTerms)
        {
            score += TextMatchScore(entity.Name, term, titleWeight: 180, detailWeight: 60);
            score += TextMatchScore(entity.Type, term, titleWeight: 16, detailWeight: 8);
            score += TextMatchScore(entity.Summary, term, titleWeight: 28, detailWeight: 14);
            foreach (var alias in entity.Aliases)
                score += TextMatchScore(alias, term, titleWeight: 70, detailWeight: 24);
            foreach (var section in entity.WikiSections)
            {
                score += TextMatchScore(section.Title, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(section.Body, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var canonSource in entity.CanonSources)
            {
                score += TextMatchScore(canonSource.SourceTitle, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(canonSource.Markdown, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var property in entity.Properties)
            {
                score += TextMatchScore(property.Key, term, titleWeight: 10, detailWeight: 5);
                score += TextMatchScore(property.Value, term, titleWeight: 10, detailWeight: 5);
            }
        }

        return score;
    }

    private static int TextMatchScore(string? value, string query, int titleWeight, int detailWeight)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query)) return 0;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 4;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 2;
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? detailWeight : 0;
    }

    private static object LinkPayload(EntityLink link) => new
    {
        edgeId = link.EdgeId,
        edgeType = link.EdgeType,
        direction = link.Direction.ToString(),
        otherEntityId = link.OtherEntityId,
        otherEntityName = link.OtherEntityName,
        otherEntityType = link.OtherEntityType,
        sortOrder = link.SortOrder,
        properties = link.Properties,
        summary = link.Summary,
        relationshipCitations = link.RelationshipCitations,
        isAutoLink = link.IsAutoLink,
    };

    private static IReadOnlyList<Guid>? ParseSourceIds(string[]? sourceIds, out string? error)
    {
        error = null;
        if (sourceIds is not { Length: > 0 }) return null;

        var parsed = new List<Guid>();
        foreach (var value in sourceIds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!Guid.TryParse(value, out var id))
            {
                error = $"Error: sourceId '{value}' is not a valid Guid.";
                return null;
            }
            parsed.Add(id);
        }

        return parsed.Count == 0 ? null : parsed;
    }

    private static object[] CompactCanonSources(IReadOnlyList<IngestCanonSource> sources) =>
        sources
            .Take(4)
            .Select(source => new
            {
                source.SourceTitle,
                source.SourceKind,
                markdown = TruncatePropertyValue(source.Markdown),
            })
            .ToArray();

    private static object[] CompactWikiSections(IReadOnlyList<IngestWikiSection> sections) =>
        sections
            .Take(4)
            .Select(section => new
            {
                section.Id,
                section.Title,
                body = TruncatePropertyValue(section.Body),
            })
            .ToArray();

    private static string? TruncatePropertyValue(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 240 ? value : value[..240] + "...";

    private static bool IsSearchableEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static List<AIContent> BuildAssistantToolCallContents(IReadOnlyList<PendingToolCall> calls) =>
        calls.Select(call => (AIContent)call.Content).ToList();

    private sealed record PendingToolCall(
        FunctionCallContent Content,
        string CallId,
        string Name,
        string ArgumentsJson,
        int TextOffset);

    private sealed record PersistedToolCall(string CallId, string Name, string ArgumentsJson, int? TextOffset = null);

    private sealed record ReadChapterLineSegment(int LineNumber, string Text);

    private sealed class ReadChapterPage
    {
        public List<ReadChapterLineSegment> Segments { get; } = [];
        public int ContentCharCount { get; private set; }
        public int PageStartLine => Segments[0].LineNumber;
        public int PageEndLine => Segments[^1].LineNumber;

        public void Add(ReadChapterLineSegment segment, int lineNumberWidth)
        {
            if (Segments.Count > 0) ContentCharCount++;
            ContentCharCount += lineNumberWidth + 2 + segment.Text.Length;
            Segments.Add(segment);
        }
    }

    private sealed class CapturedChapterEdit
    {
        public Guid ChapterId { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public long ExpectedRevision { get; set; }
        public ManuscriptOperationInput[] Operations { get; set; } = [];
        public string Notes { get; set; } = string.Empty;
    }
}

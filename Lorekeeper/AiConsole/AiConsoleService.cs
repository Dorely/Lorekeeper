using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lorekeeper.AiConsole;

public sealed class AiConsoleService(
    AppDbContext db,
    IProjectRepository projects,
    IChapterRepository chapterRepo,
    IContextBuilder contextBuilder,
    ILlmProviderService providerService,
    IChatClientFactory chatClientFactory,
    AiConsoleTools tools,
    IOptions<AiConsoleOptions> options,
    ILogger<AiConsoleService> logger) : IAiConsoleService
{
    public async Task<AiConsoleEntry> RunAsync(
        Guid projectId,
        Guid? currentChapterId,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command cannot be empty.", nameof(command));

        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        Chapter? currentChapter = null;
        if (currentChapterId is { } cid)
        {
            currentChapter = await chapterRepo.GetByIdAsync(cid, cancellationToken);
        }

        var assembly = contextBuilder.Build(project, currentChapter);
        var systemPrompt = assembly.Assemble();

        var entry = new AiConsoleEntry
        {
            ProjectId = projectId,
            ChapterId = currentChapterId,
            Command = command.Trim(),
            SystemPromptSnapshot = systemPrompt,
            Status = AiConsoleEntryStatus.Pending,
        };
        db.AiConsoleEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        var toolRecords = new List<AiToolCallRecord>();

        try
        {
            var defaultProvider = await providerService.GetDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("No default LLM provider configured.");

            var chat = await chatClientFactory.CreateChatClientAsync(defaultProvider.Id, cancellationToken);

            var aiTools = tools.Build(new AiConsoleContext(projectId, currentChapterId));
            var chatOptions = new ChatOptions
            {
                Tools = aiTools,
                ToolMode = ChatToolMode.Auto,
            };

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, command.Trim()),
            };

            var maxIterations = Math.Max(1, options.Value.MaxToolIterations);
            string? finalText = null;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await chat.GetResponseAsync(messages, chatOptions, cancellationToken);

                var assistantMessage = response.Messages.LastOrDefault()
                    ?? new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty);
                messages.Add(assistantMessage);

                var functionCalls = assistantMessage.Contents.OfType<FunctionCallContent>().ToList();
                if (functionCalls.Count == 0)
                {
                    finalText = assistantMessage.Text;
                    break;
                }

                var resultContents = new List<AIContent>();
                foreach (var fc in functionCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var argsJson = fc.Arguments is null
                        ? "{}"
                        : JsonSerializer.Serialize(fc.Arguments);

                    var startedAt = DateTime.UtcNow;
                    string? toolResult = null;
                    string? toolError = null;

                    try
                    {
                        var aiFn = aiTools.OfType<AIFunction>().FirstOrDefault(f => f.Name == fc.Name)
                            ?? throw new InvalidOperationException($"Unknown tool '{fc.Name}'.");

                        var argsDict = fc.Arguments ?? new Dictionary<string, object?>();
                        var invokeResult = await aiFn.InvokeAsync(new AIFunctionArguments(argsDict), cancellationToken);
                        toolResult = invokeResult?.ToString() ?? string.Empty;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Tool '{Tool}' failed", fc.Name);
                        toolError = ex.Message;
                        toolResult = $"Error: {ex.Message}";
                    }

                    toolRecords.Add(new AiToolCallRecord(
                        Name: fc.Name,
                        Arguments: argsJson,
                        Result: toolError is null ? toolResult : null,
                        Error: toolError,
                        StartedAt: startedAt,
                        CompletedAt: DateTime.UtcNow));

                    resultContents.Add(new FunctionResultContent(fc.CallId ?? fc.Name, toolResult ?? string.Empty));
                }

                messages.Add(new ChatMessage(ChatRole.Tool, resultContents));

                if (iteration == maxIterations - 1)
                {
                    throw new InvalidOperationException(
                        $"Tool-call loop hit configured cap of {maxIterations} iterations without producing a final response.");
                }
            }

            entry.ResponseText = finalText;
            entry.ToolCallsJson = JsonSerializer.Serialize(toolRecords);
            entry.Status = AiConsoleEntryStatus.Completed;
            entry.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return entry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry.ToolCallsJson = JsonSerializer.Serialize(toolRecords);
            entry.Status = AiConsoleEntryStatus.Cancelled;
            entry.ErrorMessage = "Cancelled by user.";
            entry.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return entry;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI Console turn failed for project {ProjectId}", projectId);
            entry.ToolCallsJson = JsonSerializer.Serialize(toolRecords);
            entry.Status = AiConsoleEntryStatus.Failed;
            entry.ErrorMessage = ex.Message;
            entry.CompletedAt = DateTime.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception saveEx) { logger.LogError(saveEx, "Failed to persist AiConsoleEntry failure"); }
            return entry;
        }
    }
}

using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.AiConsole;

public sealed class AiConsoleChangeStagingContext(
    Guid projectId,
    Guid conversationId,
    IAiChangeRepository changes)
{
    private AiChangeBatch? _batch;
    private readonly List<AiChange> _newChanges = [];
    private Guid? _entryId;
    private string _toolCallId = string.Empty;
    private string _toolName = string.Empty;
    private string _argumentsJson = "{}";
    private int _nextOrder;

    public void BeginToolCall(Guid entryId, string toolCallId, string toolName, string argumentsJson)
    {
        _entryId = entryId;
        _toolCallId = toolCallId;
        _toolName = toolName;
        _argumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
    }

    public IReadOnlyList<AiChange> DrainNewChanges()
    {
        var result = _newChanges.ToList();
        _newChanges.Clear();
        return result;
    }

    public async Task StageChapterBodyEditAsync(
        Chapter chapter,
        string newBody,
        string summary,
        string result,
        CancellationToken cancellationToken = default)
    {
        var before = new ChapterBodyChange(chapter.Id, chapter.Title, chapter.Body);
        var after = new ChapterBodyChange(chapter.Id, chapter.Title, newBody);
        await StageChangeAsync(
            summary,
            before,
            after,
            result,
            resourceKind: "ChapterBody",
            resourceId: Resource("Chapter", chapter.Id),
            referencedResources: [Resource("Chapter", chapter.Id)],
            cancellationToken);
    }

    private async Task StageChangeAsync(
        string summary,
        object? before,
        object? after,
        string resultJson,
        string resourceKind,
        string resourceId,
        IReadOnlyCollection<string> referencedResources,
        CancellationToken cancellationToken)
    {
        var batch = await EnsureBatchAsync(cancellationToken);
        var change = new AiChange
        {
            BatchId = batch.Id,
            Order = _nextOrder++,
            ToolCallId = _toolCallId,
            ToolName = _toolName,
            ArgumentsJson = _argumentsJson,
            Summary = summary,
            BeforeJson = Serialize(before),
            AfterJson = Serialize(after),
            ResultJson = resultJson,
            ResourceKind = resourceKind,
            ResourceId = resourceId,
            CreatedResourceIdsJson = "[]",
            ReferencedResourceIdsJson = Serialize(referencedResources),
            DependsOnChangeIdsJson = "[]",
        };

        await changes.AddChangeAsync(change, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        _newChanges.Add(change);
    }

    private async Task<AiChangeBatch> EnsureBatchAsync(CancellationToken cancellationToken)
    {
        if (_batch is not null) return _batch;

        _batch = new AiChangeBatch
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            AssistantMessageId = _entryId,
        };
        await changes.AddBatchAsync(_batch, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        return _batch;
    }

    private static string Resource(string kind, Guid id) => $"{kind}:{id:N}";

    private static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, JsonSerializerOptions.Default);
}
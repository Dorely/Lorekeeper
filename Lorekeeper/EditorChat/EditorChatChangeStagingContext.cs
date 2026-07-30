using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.EditorChat;

public sealed class EditorChatChangeStagingContext(
    Guid projectId,
    Guid conversationId,
    IAiChangeRepository changes)
{
    private AiChangeBatch? _batch;
    private readonly List<AiChange> _newChanges = [];
    private readonly Dictionary<Guid, ManuscriptDocument> _chapterManuscriptDrafts = [];
    private Guid? _assistantMessageId;
    private string _toolCallId = string.Empty;
    private string _toolName = string.Empty;
    private string _argumentsJson = "{}";
    private int _nextOrder;

    public void BeginToolCall(Guid assistantMessageId, string toolCallId, string toolName, string argumentsJson)
    {
        _assistantMessageId = assistantMessageId;
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

    public bool TryGetChapterBodyDraft(Guid chapterId, out string body)
    {
        if (TryGetChapterManuscriptDraft(chapterId, out var document))
        {
            body = ManuscriptCodec.ProjectPlainText(document);
            return true;
        }

        body = string.Empty;
        return false;
    }

    public bool TryGetChapterManuscriptDraft(Guid chapterId, out ManuscriptDocument document) =>
        _chapterManuscriptDrafts.TryGetValue(chapterId, out document!);

    public async Task StageChapterBodyEditAsync(
        Chapter chapter,
        string beforeBody,
        string newBody,
        string summary,
        string result,
        CancellationToken cancellationToken = default)
    {
        var beforeDocument = TryGetChapterManuscriptDraft(chapter.Id, out var staged)
            ? staged
            : string.Equals(chapter.PlainText, beforeBody, StringComparison.Ordinal)
                ? chapter.Manuscript
                : ManuscriptCodec.FromPlainText(chapter.Id, beforeBody, chapter.ManuscriptRevision);
        var afterDocument = ManuscriptCodec.ReparsePreservingBlockIds(beforeDocument, newBody);
        await StageChapterManuscriptEditAsync(
            chapter,
            beforeDocument,
            afterDocument,
            summary,
            result,
            cancellationToken);
    }

    public async Task StageChapterManuscriptEditAsync(
        Chapter chapter,
        ManuscriptDocument beforeDocument,
        ManuscriptDocument afterDocument,
        string summary,
        string result,
        CancellationToken cancellationToken = default)
    {
        if (TryGetChapterManuscriptDraft(chapter.Id, out var currentDraft)
            && (beforeDocument.Revision != currentDraft.Revision
                || !ManuscriptCodec.ContentEquals(beforeDocument, currentDraft)))
        {
            throw new ManuscriptRevisionConflictException(beforeDocument.Revision, currentDraft.Revision);
        }

        var before = new ChapterManuscriptChange(
            chapter.Id, chapter.Title, beforeDocument.Revision, ManuscriptCodec.Serialize(beforeDocument));
        var after = new ChapterManuscriptChange(
            chapter.Id, chapter.Title, afterDocument.Revision, ManuscriptCodec.Serialize(afterDocument));
        await StageChangeAsync(
            summary,
            before,
            after,
            result,
            resourceKind: "ChapterManuscript",
            resourceId: Resource("Chapter", chapter.Id),
            referencedResources: [Resource("Chapter", chapter.Id)],
            cancellationToken);
        _chapterManuscriptDrafts[chapter.Id] = afterDocument;
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
        var beforeJson = Serialize(before);
        var afterJson = Serialize(after);
        if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            return;

        var batch = await EnsureBatchAsync(cancellationToken);
        var change = new AiChange
        {
            BatchId = batch.Id,
            Order = _nextOrder++,
            ToolCallId = _toolCallId,
            ToolName = _toolName,
            ArgumentsJson = _argumentsJson,
            Summary = summary,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
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
            ConversationKind = AiChangeConversationKind.Editor,
            ConversationId = conversationId,
            AssistantMessageId = _assistantMessageId,
        };
        await changes.AddBatchAsync(_batch, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        _nextOrder = 0;
        return _batch;
    }

    private static string Resource(string kind, Guid id) => $"{kind}:{id:N}";

    private static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, JsonSerializerOptions.Default);

}

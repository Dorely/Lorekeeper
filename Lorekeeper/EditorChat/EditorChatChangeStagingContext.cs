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
    IAiChangeRepository changes) : IChapterManuscriptChangeStagingContext
{
    private static readonly JsonSerializerOptions ChangeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private AiChangeBatch? _batch;
    private readonly List<AiChange> _newChanges = [];
    private readonly HashSet<Guid> _adoptedChangeIds = [];
    private readonly Dictionary<Guid, ManuscriptDocument> _chapterManuscriptDrafts = [];
    private readonly Dictionary<Guid, ManuscriptStyleView> _manuscriptStyleDrafts = [];
    private readonly Dictionary<Guid, Guid> _manuscriptStyleProducerChanges = [];
    private bool _manuscriptStylesLoaded;
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

    public bool HasStagedManuscriptEdits => _chapterManuscriptDrafts.Count > 0;

    public void AdoptChapterManuscriptChange(AiChange change)
    {
        if (_adoptedChangeIds.Contains(change.Id))
            return;
        if (change.Status != AiChangeStatus.Pending)
            throw new InvalidOperationException($"AI change {change.Id:N} is not pending.");
        if (change.ToolName != "apply_assigned_manuscript_operations"
            || change.ResourceKind != "ChapterManuscript")
        {
            throw new InvalidOperationException("Only pending revision-worker chapter manuscript changes can be adopted into the editor turn.");
        }

        var before = DeserializeChapterChange(change.BeforeJson);
        var after = DeserializeChapterChange(change.AfterJson);
        if (before.Id != after.Id
            || !string.Equals(change.ResourceId, Resource("Chapter", after.Id), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The revision-worker change does not identify one valid chapter manuscript.");
        }

        var beforeDocument = before.Manuscript;
        var afterDocument = after.Manuscript;
        if (afterDocument.Revision != checked(beforeDocument.Revision + 1))
            throw new InvalidDataException("The revision-worker change does not advance the manuscript revision exactly once.");

        if (TryGetChapterManuscriptDraft(after.Id, out var currentDraft))
        {
            throw new ManuscriptRevisionConflictException(beforeDocument.Revision, currentDraft.Revision);
        }

        _chapterManuscriptDrafts.Add(after.Id, afterDocument);
        _adoptedChangeIds.Add(change.Id);
        _newChanges.Add(change);
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
        var usedParagraphRoles = afterDocument.Content
            .Select(block => block.StyleRole)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedCharacterRoles = afterDocument.Content
            .SelectMany(block => block.Content)
            .SelectMany(inline => inline.Marks)
            .Where(mark => mark.Type == ManuscriptMarkType.CharacterStyle)
            .Select(mark => mark.Value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedStyles = _manuscriptStyleDrafts.Values
            .Where(style => style.Kind == ManuscriptStyleKind.Paragraph
                ? usedParagraphRoles.Contains(style.SemanticRole)
                : usedCharacterRoles.Contains(style.SemanticRole))
            .ToList();
        var styleDependencies = usedStyles
            .Where(style => _manuscriptStyleProducerChanges.ContainsKey(style.Id))
            .Select(style => _manuscriptStyleProducerChanges[style.Id])
            .Distinct()
            .ToList();
        var referencedResources = usedStyles
            .Select(style => Resource("ManuscriptStyle", style.Id))
            .Append(Resource("Chapter", chapter.Id))
            .ToList();
        await StageChangeAsync(
            summary,
            before,
            after,
            result,
            resourceKind: "ChapterManuscript",
            resourceId: Resource("Chapter", chapter.Id),
            referencedResources,
            cancellationToken,
            dependsOnChangeIds: styleDependencies);
        _chapterManuscriptDrafts[chapter.Id] = afterDocument;
    }

    public async Task<IReadOnlyList<ManuscriptStyleView>> ListManuscriptStyleDraftsAsync(
        IManuscriptStyleService styles,
        CancellationToken cancellationToken = default)
    {
        if (!_manuscriptStylesLoaded)
        {
            foreach (var style in await styles.ListAsync(projectId, cancellationToken))
                _manuscriptStyleDrafts[style.Id] = style;
            _manuscriptStylesLoaded = true;
        }
        return _manuscriptStyleDrafts.Values
            .OrderBy(style => style.Kind)
            .ThenBy(style => style.Name)
            .ToList();
    }

    public async Task StageManuscriptStyleChangeAsync(
        ManuscriptStyleView? before,
        ManuscriptStyleInput? after,
        ManuscriptStyleView? preview,
        string summary,
        string result,
        CancellationToken cancellationToken = default)
    {
        var id = before?.Id ?? after?.Id;
        var resource = id is Guid styleId ? Resource("ManuscriptStyle", styleId) : $"ManuscriptStyle:new:{after?.SemanticRole}";
        var isCreate = before is null && after is not null && after.ExpectedRevision is null;
        var dependsOn = id is Guid dependentId
            && _manuscriptStyleProducerChanges.TryGetValue(dependentId, out var producerId)
                ? new[] { producerId }
                : [];
        var current = before is null
            ? null
            : new ManuscriptStyleInput(
                before.Id,
                before.Name,
                before.Kind,
                before.SemanticRole,
                before.Definition,
                before.Revision);
        var change = await StageChangeAsync(
            summary,
            new ManuscriptStyleChange(before, current),
            new ManuscriptStyleChange(before, after),
            result,
            resourceKind: "ManuscriptStyle",
            resourceId: resource,
            referencedResources: [resource],
            cancellationToken,
            createdResources: isCreate ? [resource] : [],
            dependsOnChangeIds: dependsOn);
        if (id is Guid changedId && change is not null)
            _manuscriptStyleProducerChanges[changedId] = change.Id;
        if (preview is not null)
            _manuscriptStyleDrafts[preview.Id] = preview;
        else if (before is not null)
            _manuscriptStyleDrafts.Remove(before.Id);
        _manuscriptStylesLoaded = true;
    }

    private async Task<AiChange?> StageChangeAsync(
        string summary,
        object? before,
        object? after,
        string resultJson,
        string resourceKind,
        string resourceId,
        IReadOnlyCollection<string> referencedResources,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? createdResources = null,
        IReadOnlyCollection<Guid>? dependsOnChangeIds = null)
    {
        var beforeJson = Serialize(before);
        var afterJson = Serialize(after);
        if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            return null;

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
            CreatedResourceIdsJson = Serialize(createdResources ?? []),
            ReferencedResourceIdsJson = Serialize(referencedResources),
            DependsOnChangeIdsJson = Serialize(dependsOnChangeIds ?? []),
        };

        await changes.AddChangeAsync(change, cancellationToken);
        await changes.SaveChangesAsync(cancellationToken);
        _newChanges.Add(change);
        return change;
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

    private static ChapterManuscriptChange DeserializeChapterChange(string json) =>
        JsonSerializer.Deserialize<ChapterManuscriptChange>(json, ChangeJsonOptions)
        ?? throw new InvalidDataException("The revision-worker change payload is missing its manuscript state.");

    private static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, JsonSerializerOptions.Default);

}

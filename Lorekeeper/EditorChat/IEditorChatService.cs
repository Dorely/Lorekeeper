using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Images;

namespace Lorekeeper.EditorChat;

public interface IEditorChatService
{
    Task<EditorConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<bool> GetAiChangeApprovalEnabledAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetAiChangeApprovalEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task<EditorContestSettings> GetContestSettingsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AiChangeBatch>> ListPendingChangesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContestBatch>> ListCurrentContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task ResolveContestCandidateLineAsync(Guid projectId, Guid chapterId, ContestCandidateReviewLineResolution request, CancellationToken cancellationToken = default);
    Task KeepContestCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
    Task FinishContestBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorChatTurnUpdate> SendAsync(Guid projectId, Guid? currentChapterId, string userText, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class EditorChatContext(
    Guid projectId,
    Guid conversationId,
    Guid? currentChapterId,
    int providerId,
    bool visionReady,
    Action onMutated,
    bool reviewEdits,
    bool autoPinReadEntities,
    OutlineToolStagingContext? outlineStaging,
    EditorChatChangeStagingContext? editorStaging,
    CancellationToken turnCancellationToken)
{
    private readonly object _imageGenerationLock = new();
    private readonly HashSet<Guid> _imageGenerationJobIds = [];
    private EditorContestStartRequest? _contestRequest;
    private Guid? _currentImageGenerationJobId;
    private readonly HashSet<Guid> _directlyEditedChapterBodies = [];
    private readonly List<EditorChatVisualAttachment> _visuals = [];
    private readonly List<EditorChatModelImageAttachment> _modelOnlyImages = [];

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public Guid? CurrentChapterId { get; } = currentChapterId;
    public int ProviderId { get; } = providerId;
    public bool VisionReady { get; } = visionReady;
    public Action OnMutated { get; } = onMutated;
    public bool ReviewEdits { get; } = reviewEdits;
    public bool AutoPinReadEntities { get; } = autoPinReadEntities;
    public OutlineToolStagingContext? OutlineStaging { get; } = outlineStaging;
    public EditorChatChangeStagingContext? EditorStaging { get; } = editorStaging;
    public CancellationToken TurnCancellationToken { get; } = turnCancellationToken;
    public Guid? CurrentAssistantMessageId { get; private set; }
    public string CurrentToolCallId { get; private set; } = string.Empty;
    public string CurrentToolName { get; private set; } = string.Empty;
    public string CurrentArgumentsJson { get; private set; } = "{}";
    public Guid? CurrentImageGenerationJobId
    {
        get
        {
            lock (_imageGenerationLock)
                return _currentImageGenerationJobId;
        }
    }

    public bool ShouldBypassReviewForChapterBody(Chapter chapter) =>
        _directlyEditedChapterBodies.Contains(chapter.Id)
        || string.IsNullOrWhiteSpace(chapter.Body);

    public void MarkChapterBodyDirectlyEdited(Guid chapterId) =>
        _directlyEditedChapterBodies.Add(chapterId);

    public void BeginToolCall(Guid assistantMessageId, string toolCallId, string toolName, string argumentsJson)
    {
        CurrentAssistantMessageId = assistantMessageId;
        CurrentToolCallId = toolCallId;
        CurrentToolName = toolName;
        CurrentArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        lock (_imageGenerationLock)
            _currentImageGenerationJobId = null;
        OutlineStaging?.BeginToolCall(assistantMessageId, toolCallId, toolName, argumentsJson);
        EditorStaging?.BeginToolCall(assistantMessageId, toolCallId, toolName, argumentsJson);
    }

    public void TrackImageGenerationJob(Guid jobId)
    {
        lock (_imageGenerationLock)
        {
            _currentImageGenerationJobId = jobId;
            _imageGenerationJobIds.Add(jobId);
        }
    }

    public IReadOnlyList<Guid> ImageGenerationJobIds
    {
        get
        {
            lock (_imageGenerationLock)
                return _imageGenerationJobIds.ToList();
        }
    }

    public void AddVisual(EditorChatVisualAttachment visual) => _visuals.Add(visual);

    public IReadOnlyList<EditorChatVisualAttachment> DrainVisuals(string toolCallId)
    {
        var matched = _visuals.Where(visual => string.Equals(visual.ToolCallId, toolCallId, StringComparison.Ordinal)).ToList();
        if (matched.Count == 0)
            return [];

        foreach (var visual in matched)
            _visuals.Remove(visual);
        return matched;
    }

    public void AddModelOnlyImage(ProjectImageView image)
    {
        if (VisionReady)
        {
            _modelOnlyImages.Add(new EditorChatModelImageAttachment(
                image.Id,
                image.Id,
                image.FileName,
                image.ContentType,
                Data: null));
        }
    }

    public void AddModelOnlyImage(Guid id, string fileName, string contentType, byte[] data)
    {
        if (VisionReady)
            _modelOnlyImages.Add(new EditorChatModelImageAttachment(id, ProjectImageId: null, fileName, contentType, data));
    }

    public IReadOnlyList<EditorChatModelImageAttachment> DrainModelOnlyImages()
    {
        if (_modelOnlyImages.Count == 0)
            return [];

        var result = _modelOnlyImages.ToList();
        _modelOnlyImages.Clear();
        return result;
    }

    public void RequestContest(EditorContestStartRequest request)
    {
        if (_contestRequest is not null)
            throw new InvalidOperationException("start_contest can only be called once per editor chat turn.");
        _contestRequest = request;
    }

    public bool TryTakeContestRequest(out EditorContestStartRequest request)
    {
        if (_contestRequest is null)
        {
            request = null!;
            return false;
        }

        request = _contestRequest;
        _contestRequest = null;
        return true;
    }
}

public sealed record EditorChatModelImageAttachment(
    Guid Id,
    Guid? ProjectImageId,
    string FileName,
    string ContentType,
    byte[]? Data);

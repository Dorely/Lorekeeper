using Lorekeeper.Models;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Llm;

namespace Lorekeeper.EditorChat;

public interface IEditorChatService
{
    Task<EditorConversation> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EditorMessage>> LoadMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task<ChatProviderAvailability> GetChatProviderAvailabilityAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetSelectedProviderAsync(Guid projectId, int? providerId, CancellationToken cancellationToken = default);
    Task<EditorContestSettings> GetContestSettingsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SetContestModeEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default);
    Task SetContestProviderAsync(Guid projectId, int slot, int? providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContestBatch>> ListCurrentContestBatchesAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<EditorContestReviewSnapshot?> GetContestReviewAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task<EditorContestLockState> GetEditorContestLockStateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SelectContestCandidateAsync(Guid projectId, Guid batchId, Guid candidateId, CancellationToken cancellationToken = default);
    Task ResetContestCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default);
    Task ResolveContestCandidateLineAsync(Guid projectId, Guid chapterId, ContestCandidateReviewLineResolution request, CancellationToken cancellationToken = default);
    Task ResolveContestAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task DiscardContestAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task CancelContestAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    Task KeepContestCandidateAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken = default);
    Task FinishContestBatchAsync(Guid projectId, Guid batchId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EditorChatTurnUpdate> SendAsync(Guid projectId, Guid? currentChapterId, Guid? currentCompositionId, EditorContentTarget contentTarget, string userText, IReadOnlyList<Guid> imageIds, int providerId, CancellationToken cancellationToken = default);
    Task ResetAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public sealed class EditorChatContext(
    Guid projectId,
    Guid conversationId,
    Guid? currentChapterId,
    Guid? currentCompositionId,
    EditorContentTarget contentTarget,
    int providerId,
    bool visionReady,
    Action onMutated,
    bool reviewEdits,
    bool autoPinReadEntities,
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
    public Guid? CurrentCompositionId { get; } = currentCompositionId;
    public EditorContentTarget ContentTarget { get; } = contentTarget;
    public int ProviderId { get; } = providerId;
    public bool VisionReady { get; } = visionReady;
    public Action OnMutated { get; } = onMutated;
    public bool ReviewEdits { get; } = reviewEdits;
    public bool AutoPinReadEntities { get; } = autoPinReadEntities;
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
        || string.IsNullOrWhiteSpace(chapter.PlainText);

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

using Lorekeeper.Images;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatToolContext(
    Guid projectId,
    Guid conversationId,
    int providerId,
    bool visionReady,
    IReadOnlyList<ProjectImageView> attachedImages,
    Action onMutated,
    CancellationToken turnCancellationToken)
{
    private readonly List<ImagesChatVisualAttachment> _visuals = [];
    private readonly List<ProjectImageView> _modelOnlyImages = [];
    private readonly List<ImagesChatModelOnlyImage> _modelOnlyImagePayloads = [];
    private readonly HashSet<Guid> _fullResolutionImageIds = [];
    private readonly HashSet<Guid> _imageGenerationJobIds = [];
    private readonly IReadOnlyDictionary<Guid, ProjectImageView> _attachedImages = attachedImages
        .DistinctBy(image => image.Id)
        .ToDictionary(image => image.Id);

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public int ProviderId { get; } = providerId;
    public bool VisionReady { get; } = visionReady;
    public CancellationToken TurnCancellationToken { get; } = turnCancellationToken;
    public string CurrentToolCallId { get; private set; } = string.Empty;
    public string CurrentToolName { get; private set; } = string.Empty;
    public string CurrentArgumentsJson { get; private set; } = "{}";

    public void BeginToolCall(string toolCallId, string toolName, string argumentsJson)
    {
        CurrentToolCallId = toolCallId;
        CurrentToolName = toolName;
        CurrentArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
    }

    public void AddVisual(ImagesChatVisualAttachment visual) => _visuals.Add(visual);

    public IReadOnlyList<ImagesChatVisualAttachment> DrainVisuals(string toolCallId)
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
            _modelOnlyImages.Add(image);
    }

    public IReadOnlyList<ProjectImageView> DrainModelOnlyImages()
    {
        if (_modelOnlyImages.Count == 0)
            return [];

        var result = _modelOnlyImages.ToList();
        _modelOnlyImages.Clear();
        return result;
    }

    public ProjectImageView? FindAttachedImage(Guid imageId) =>
        _attachedImages.GetValueOrDefault(imageId);

    public void RequestFullResolution(Guid imageId) => _fullResolutionImageIds.Add(imageId);

    public IReadOnlySet<Guid> FullResolutionImageIds => _fullResolutionImageIds;

    public void AddModelOnlyImage(ProjectImageView image, byte[] data)
    {
        if (VisionReady && data.Length > 0)
            _modelOnlyImagePayloads.Add(new ImagesChatModelOnlyImage(image, data));
    }

    public IReadOnlyList<ImagesChatModelOnlyImage> DrainModelOnlyImagePayloads()
    {
        var result = _modelOnlyImagePayloads.ToList();
        _modelOnlyImagePayloads.Clear();
        return result;
    }

    public void MarkMutated() => onMutated();

    public void TrackImageGenerationJob(Guid jobId) => _imageGenerationJobIds.Add(jobId);

    public IReadOnlyList<Guid> ImageGenerationJobIds => _imageGenerationJobIds.ToList();
}

public sealed record ImagesChatModelOnlyImage(ProjectImageView Image, byte[] Data);

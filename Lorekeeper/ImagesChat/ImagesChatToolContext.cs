using Lorekeeper.Images;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatToolContext(
    Guid projectId,
    Guid conversationId,
    int providerId,
    bool visionReady,
    Action onMutated)
{
    private readonly List<ImagesChatVisualAttachment> _visuals = [];
    private readonly List<ProjectImageView> _modelOnlyImages = [];

    public Guid ProjectId { get; } = projectId;
    public Guid ConversationId { get; } = conversationId;
    public int ProviderId { get; } = providerId;
    public bool VisionReady { get; } = visionReady;
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

    public void MarkMutated() => onMutated();
}

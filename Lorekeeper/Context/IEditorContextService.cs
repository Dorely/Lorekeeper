using Lorekeeper.Models;
using Lorekeeper.Outline;

namespace Lorekeeper.Context;

public interface IEditorContextService : IContextBuilder
{
    Task SetItemIncludedAsync(
        Guid projectId,
        Guid chapterId,
        ContextItemKind kind,
        string key,
        bool isIncluded,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoryEntity>> ListAutoRelatedEntitiesAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Guid>> ListIncludedEntityIdsAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default);
}

public static class EditorContextKeys
{
    public const string SystemPrompt = "system-prompt";
    public const string CurrentChapter = "current-chapter";
    public const string ProjectOutline = "project-outline";
    public const string ProjectFacts = "project-facts";

    public static string WritingSample(Guid sampleId) => $"writing-sample:{sampleId:N}";
    public static string Entity(Guid entityId) => $"entity:{entityId:N}";

    public static bool TryParseEntity(string key, out Guid entityId)
    {
        const string prefix = "entity:";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(key[prefix.Length..], "N", out entityId))
        {
            return true;
        }

        entityId = Guid.Empty;
        return false;
    }
}
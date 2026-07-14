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

    Task<IReadOnlyCollection<string>> ListIncludedContextKeysAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default);
}

public static class EditorContextKeys
{
    public const string SystemInstructions = "system-instructions";
    public const string AssistantWorkflow = "assistant-workflow";
    public const string DynamicGuidance = "dynamic-guidance";
    public const string ProjectGuidance = "project-guidance";
    public const string BookBrief = "book-brief";
    public const string CurrentChapter = "current-chapter";
    public const string ProjectOutline = "project-outline";
    public const string ProjectFacts = "project-facts";

    public static string WritingSample(Guid sampleId) => $"writing-sample:{sampleId:N}";
    public static string Entity(Guid entityId) => $"entity:{entityId:N}";
    public static string ChapterReference(Guid chapterId) => $"chapter:{chapterId:N}";
    public static string ActReference(Guid actId) => $"act:{actId:N}";
    public static string IngestSourceReference(Guid sourceId) => $"ingest-source:{sourceId:N}";
    public static string IngestSourceChunkReference(Guid sourceChunkId) => $"ingest-source-chunk:{sourceChunkId:N}";
    public static string ProjectImage(Guid imageId) => $"project-image:{imageId:N}";
    public static string ChapterVisualLayout(Guid chapterId) => $"chapter-visual-layout:{chapterId:N}";

    public static bool TryParseEntity(string key, out Guid entityId)
    {
        const string prefix = "entity:";
        if (TryParseGuidKey(key, prefix, out entityId))
        {
            return true;
        }

        entityId = Guid.Empty;
        return false;
    }

    public static bool TryParseChapterReference(string key, out Guid chapterId) =>
        TryParseGuidKey(key, "chapter:", out chapterId);

    public static bool TryParseActReference(string key, out Guid actId) =>
        TryParseGuidKey(key, "act:", out actId);

    public static bool TryParseIngestSourceReference(string key, out Guid sourceId) =>
        TryParseGuidKey(key, "ingest-source:", out sourceId);

    public static bool TryParseIngestSourceChunkReference(string key, out Guid sourceChunkId) =>
        TryParseGuidKey(key, "ingest-source-chunk:", out sourceChunkId);

    public static bool TryParseProjectImage(string key, out Guid imageId) =>
        TryParseGuidKey(key, "project-image:", out imageId);

    private static bool TryParseGuidKey(string key, string prefix, out Guid id)
    {
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(key[prefix.Length..], "N", out id))
        {
            return true;
        }

        id = Guid.Empty;
        return false;
    }
}

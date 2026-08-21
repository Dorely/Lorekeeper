namespace Lorekeeper.Context;

public static class ContextVectorSourceTypes
{
    public const string Entity = "context_entity";
    public const string Chapter = "context_chapter";
    public const string Act = "context_act";
    public const string IngestSource = "context_ingest_source";
    public const string IngestSourceChunk = "context_ingest_source_chunk";
    public const string ProjectProfile = "context_project_profile";
    public const string WritingSample = "context_writing_sample";

    public static readonly IReadOnlyList<string> All =
    [
        Entity,
        Chapter,
        Act,
        IngestSource,
        IngestSourceChunk,
        ProjectProfile,
        WritingSample,
    ];
}

public interface IContextIndexingService
{
    Task ReindexEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default);
    Task DeleteEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default);
    Task ReindexChapterAsync(Guid chapterId, CancellationToken cancellationToken = default);
    Task DeleteChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default);
    Task ReindexActAsync(Guid actId, CancellationToken cancellationToken = default);
    Task DeleteActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default);
    Task ReindexIngestSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task DeleteIngestSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default);
    Task ReindexIngestSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default);
    Task DeleteIngestSourceChunkAsync(Guid projectId, Guid sourceChunkId, CancellationToken cancellationToken = default);
    Task ReindexProjectProfileAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DeleteProjectProfileAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task ReindexWritingSampleAsync(Guid sampleId, CancellationToken cancellationToken = default);
    Task DeleteWritingSampleAsync(Guid projectId, Guid sampleId, CancellationToken cancellationToken = default);
}

using Lorekeeper.Knowledge;

namespace Lorekeeper.Search;

public static class ProjectSearchSourceTypes
{
    public const string Chapter = "chapter";
    public const string RawIngestSource = "ingest_source";
    public const string Entity = Context.ContextVectorSourceTypes.Entity;
    public const string ContextChapter = Context.ContextVectorSourceTypes.Chapter;
    public const string Act = Context.ContextVectorSourceTypes.Act;
    public const string IngestSource = Context.ContextVectorSourceTypes.IngestSource;
    public const string IngestSourceChunk = Context.ContextVectorSourceTypes.IngestSourceChunk;

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Chapter,
        RawIngestSource,
        Entity,
        ContextChapter,
        Act,
        IngestSource,
        IngestSourceChunk,
    };

    public static string Normalize(string sourceType)
    {
        var value = (sourceType ?? string.Empty).Trim();
        if (value.Length == 0)
            throw new ArgumentException("Source type is required.", nameof(sourceType));

        return value.ToLowerInvariant() switch
        {
            "chapter" or "chapters" => Chapter,
            "context_chapter" => ContextChapter,
            "entity" or "entities" or "graph_entity" or "context_entity" => Entity,
            "act" or "acts" or "context_act" => Act,
            "source" or "sources" or "ingestsource" or "ingest_source_reference" or "context_ingest_source" => IngestSource,
            "sourcechunk" or "source_chunk" or "source chunks" or "ingest_source_chunk" or "context_ingest_source_chunk" => IngestSourceChunk,
            "rawsource" or "raw_source" or "raw_ingest_source" or "ingest_source" => RawIngestSource,
            _ => value,
        };
    }

    public static bool IsKnown(string sourceType) =>
        All.Contains(Normalize(sourceType));
}

public sealed record ProjectSearchRequest(
    Guid ProjectId,
    string Query,
    int TopK = 10,
    IReadOnlyCollection<string>? SourceTypes = null,
    IReadOnlyCollection<Guid>? SourceIds = null,
    Guid? ContainerSourceId = null,
    bool LexicalOnly = false);

public sealed record ProjectSearchResult(
    string SourceType,
    Guid? SourceId,
    Guid? ContainerSourceId,
    string Title,
    string Content,
    string Snippet,
    string? Metadata,
    int? ChunkIndex,
    double? LexicalRank,
    int? LexicalPosition,
    double? VectorDistance,
    int? VectorPosition,
    double Score,
    IReadOnlyList<string> Reasons);

public sealed record ProjectSearchSource(
    string SourceType,
    Guid SourceId,
    Guid? ContainerSourceId,
    string Title,
    string Subtitle,
    string Preview);

public sealed record ProjectSourceReadResult(
    string SourceType,
    Guid SourceId,
    Guid? ContainerSourceId,
    string Title,
    int PageNumber,
    int PageCount,
    bool HasPreviousPage,
    bool HasNextPage,
    string Content);

public sealed record ProjectLexicalSearchRequest(
    string ScopeKey,
    string Query,
    int TopK,
    IReadOnlyCollection<string>? SourceTypes = null,
    IReadOnlyCollection<string>? SourceIds = null,
    string? ContainerSourceId = null);

public sealed record ProjectLexicalSearchResult(
    long RowId,
    string SourceType,
    string? SourceId,
    string? ContainerSourceId,
    string Title,
    string Content,
    string Snippet,
    string? Metadata,
    int? ChunkIndex,
    double Rank);

public sealed record ProjectSearchIndexChunk(
    string Content,
    string SourceType,
    string ScopeKey,
    string? SourceId,
    string? ContainerSourceId,
    string? Title,
    string? Metadata,
    int? ChunkIndex);

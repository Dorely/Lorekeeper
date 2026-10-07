using Lorekeeper.Knowledge;

namespace Lorekeeper.Search;

public static class ProjectSearchSourceTypes
{
    public const string Chapter = "chapter";
    public const string EditionChapter = "edition_chapter";
    public const string RawIngestSource = "ingest_source";
    public const string ProjectProfile = Context.ContextVectorSourceTypes.ProjectProfile;
    public const string WritingSample = Context.ContextVectorSourceTypes.WritingSample;
    public const string Entity = Context.ContextVectorSourceTypes.Entity;
    public const string ContextChapter = Context.ContextVectorSourceTypes.Chapter;
    public const string Act = Context.ContextVectorSourceTypes.Act;
    public const string IngestSource = Context.ContextVectorSourceTypes.IngestSource;
    public const string IngestSourceChunk = Context.ContextVectorSourceTypes.IngestSourceChunk;
    public const string DesignedPage = "designed_page";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Chapter,
        EditionChapter,
        RawIngestSource,
        ProjectProfile,
        WritingSample,
        Entity,
        ContextChapter,
        Act,
        IngestSource,
        IngestSourceChunk,
        DesignedPage,
    };

    public static readonly IReadOnlySet<string> DirectReferenceNarrativeTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Chapter,
            ProjectProfile,
            WritingSample,
            Entity,
            ContextChapter,
            Act,
            IngestSource,
            RawIngestSource,
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
            "edition_chapter" or "edition chapter" or "edition chapters" => EditionChapter,
            "context_chapter" => ContextChapter,
            "entity" or "entities" or "graph_entity" or "context_entity" => Entity,
            "act" or "acts" or "context_act" => Act,
            "source" or "sources" or "ingestsource" or "ingest_source_reference" or "context_ingest_source" => IngestSource,
            "sourcechunk" or "source_chunk" or "source chunks" or "ingest_source_chunk" or "context_ingest_source_chunk" => IngestSourceChunk,
            "rawsource" or "raw_source" or "raw_ingest_source" or "ingest_source" => RawIngestSource,
            "project_profile" or "context_project_profile" or "project profile" or "profile" => ProjectProfile,
            "writing_sample" or "context_writing_sample" or "writing sample" or "writingsample" => WritingSample,
            "designed_page" or "designed page" or "designedpage" or "page" or "pages" => DesignedPage,
            _ => value,
        };
    }
}

public sealed record ProjectSearchRequest(
    Guid ProjectId,
    string Query,
    int TopK = 10,
    IReadOnlyCollection<string>? SourceTypes = null,
    IReadOnlyCollection<Guid>? SourceIds = null,
    Guid? ContainerSourceId = null,
    bool LexicalOnly = false,
    bool IncludeReferencedProjects = false);

public sealed record ProjectSearchResponse(
    IReadOnlyList<ProjectSearchResult> Results,
    int TotalMatches,
    bool TotalMatchesIsExact,
    int RequestedLimit);

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
    IReadOnlyList<string> Reasons,
    Guid OriginProjectId = default,
    string OriginProjectName = "",
    string OriginProjectSlug = "",
    bool IsReferenced = false);

public sealed record ProjectSearchSource(
    string SourceType,
    Guid SourceId,
    Guid? ContainerSourceId,
    string Title,
    string Subtitle,
    string Preview,
    Guid OriginProjectId = default,
    string OriginProjectName = "",
    string OriginProjectSlug = "",
    bool IsReferenced = false);

public sealed record ProjectSearchSourceResponse(
    IReadOnlyList<ProjectSearchSource> Sources,
    int TotalMatches,
    bool TotalMatchesIsExact,
    int RequestedLimit);

public sealed record ProjectSourceReadResult(
    string SourceType,
    Guid SourceId,
    Guid? ContainerSourceId,
    string Title,
    int PageNumber,
    int PageCount,
    bool HasPreviousPage,
    bool HasNextPage,
    string Content,
    Guid OriginProjectId = default,
    string OriginProjectName = "",
    string OriginProjectSlug = "",
    bool IsReferenced = false);

public sealed record ProjectLexicalSearchRequest(
    string ScopeKey,
    string Query,
    int TopK,
    IReadOnlyCollection<string>? SourceTypes = null,
    IReadOnlyCollection<string>? SourceIds = null,
    string? ContainerSourceId = null,
    IReadOnlyCollection<string>? ScopeKeys = null);

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
    double Rank,
    string ScopeKey);

public sealed record ProjectSearchIndexChunk(
    string Content,
    string SourceType,
    string ScopeKey,
    string? SourceId,
    string? ContainerSourceId,
    string? Title,
    string? Metadata,
    int? ChunkIndex);

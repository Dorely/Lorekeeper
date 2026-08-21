using System.Text.Json;

namespace Lorekeeper.Search;

/// <summary>
/// Builds explicitly compact model-facing discovery payloads. Search previews are never presented
/// as complete source content, and every addressable result includes exact read arguments.
/// </summary>
public static class ProjectSearchAgentPayload
{
    private const int ContentPreviewMaxChars = 1_800;

    public static string SerializeResults(string query, ProjectSearchResponse response) =>
        JsonSerializer.Serialize(new
        {
            query,
            resultKind = "compactDiscovery",
            response.RequestedLimit,
            response.TotalMatches,
            response.TotalMatchesIsExact,
            returnedCount = response.Results.Count,
            isComplete = response.TotalMatchesIsExact && response.Results.Count == response.TotalMatches,
            note = "Result text is an explicitly labeled preview. Use read_project_source with detailReadArguments for complete paginated content.",
            results = response.Results.Select(ResultPayload),
        });

    public static string SerializeSources(ProjectSearchSourceResponse response) =>
        JsonSerializer.Serialize(new
        {
            resultKind = "compactDiscovery",
            response.RequestedLimit,
            response.TotalMatches,
            response.TotalMatchesIsExact,
            returnedCount = response.Sources.Count,
            isComplete = response.TotalMatchesIsExact && response.Sources.Count == response.TotalMatches,
            note = "previewText is discovery-only and may omit source detail. Use read_project_source with detailReadArguments for complete paginated content.",
            sources = response.Sources.Select(source => new
            {
                source.SourceType,
                source.SourceId,
                source.ContainerSourceId,
                source.Title,
                source.Subtitle,
                previewText = source.Preview,
                previewIsComplete = false,
                originProjectId = source.OriginProjectId,
                originProjectName = source.OriginProjectName,
                originProjectSlug = source.OriginProjectSlug,
                isReferenced = source.IsReferenced,
                detailReadTool = "read_project_source",
                detailReadArguments = new { sourceType = source.SourceType, sourceId = source.SourceId, pageNumber = 1, originProjectId = source.OriginProjectId },
            }),
        });

    private static object ResultPayload(ProjectSearchResult result)
    {
        var preview = Preview(result.Content, ContentPreviewMaxChars);
        return new
        {
            result.SourceType,
            result.SourceId,
            result.ContainerSourceId,
            result.Title,
            result.OriginProjectId,
            result.OriginProjectName,
            result.OriginProjectSlug,
            result.IsReferenced,
            snippetPreview = result.Snippet,
            result.Metadata,
            result.ChunkIndex,
            result.LexicalRank,
            result.LexicalPosition,
            result.VectorDistance,
            result.VectorPosition,
            result.Score,
            result.Reasons,
            contentPreview = new
            {
                text = preview,
                totalCharacters = result.Content.Length,
                returnedCharacters = preview.Length,
                isComplete = preview.Length == result.Content.Length,
            },
            detailReadTool = result.SourceId is null ? null : "read_project_source",
            detailReadArguments = result.SourceId is Guid sourceId
                ? new { sourceType = result.SourceType, sourceId, pageNumber = 1, originProjectId = result.OriginProjectId }
                : null,
        };
    }

    private static string Preview(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}

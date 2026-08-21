using System.Text.Json;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Outline;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Writing;

public sealed class WritingCoachContext(
    Guid projectId,
    string? currentSampleTitle,
    string? currentSampleBody,
    bool visionReady = false)
{
    private readonly List<ReferenceVisualReadResult> _referenceVisuals = [];
    public Guid ProjectId { get; } = projectId;
    public string? CurrentSampleTitle { get; } = currentSampleTitle;
    public string? CurrentSampleBody { get; } = currentSampleBody;
    public bool VisionReady { get; } = visionReady;
    public void QueueReferenceVisual(ReferenceVisualReadResult visual) { if (visual.DataDelivered) _referenceVisuals.Add(visual); }
    public IReadOnlyList<ReferenceVisualReadResult> DrainReferenceVisuals()
    {
        var result = _referenceVisuals.ToList();
        _referenceVisuals.Clear();
        return result;
    }
}

public sealed class WritingCoachTools(
    IProjectFactService projectFacts,
    IEntityRelationContextService entityRelations,
    IProjectSearchService projectSearch,
    IReferenceVisualService referenceVisuals)
{
    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    public IList<AITool> Build(WritingCoachContext context)
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create(
                method: () => ReadCurrentSection(context),
                name: "read_current_section",
                description: "Read the latest current writing sample section from the editor as JSON. This is read-only and cannot modify stored samples."),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(context),
                name: "list_project_facts",
                description: "Read project-level facts as JSON so coaching can stay grounded in the project's premise, tone, constraints, and other established truths. This is read-only."),

            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) => ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Discover bounded active-project and direct-reference sources with exact origin-qualified read arguments. References are read-only continuity evidence."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 8, string[]? sourceTypes = null, string[]? sourceIds = null, Guid? containerSourceId = null, bool lexicalOnly = false) => SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Search the active project and direct references with compact origin provenance and exact detail-read arguments. References are read-only."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null, Guid? originProjectId = null) => ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber, originProjectId),
                name: "read_project_source",
                description: "Read one paginated active-project or direct-reference source. Pass originProjectId exactly as returned by discovery; arbitrary foreign IDs are rejected."),

            AIFunctionFactory.Create(
                method: () => ListReferenceVisualsAsync(context),
                name: "list_reference_visuals",
                description: "List canonical entity visuals from direct referenced projects only with project/entity/image provenance."),

            AIFunctionFactory.Create(
                method: (Guid originProjectId, Guid imageId) => ReadReferenceVisualAsync(context, originProjectId, imageId),
                name: "read_reference_visual",
                description: "Read one canonical visual attached to a direct referenced project. Arbitrary foreign or general-library images fail closed; bytes are read-only and never valid for placement or mutation."),
        };
    }

    private async Task<string> ListSearchSourcesAsync(WritingCoachContext context, string? query, string[]? sourceTypes, int topK)
    {
        var sources = await projectSearch.ListSourcesAsync(context.ProjectId, query, sourceTypes, Math.Clamp(topK, 1, 30), includeReferencedProjects: true);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ReadProjectSourceAsync(WritingCoachContext context, string sourceType, Guid sourceId, int? pageNumber, Guid? originProjectId)
    {
        var result = await projectSearch.ReadSourceAsync(context.ProjectId, sourceType, sourceId, pageNumber, originProjectId: originProjectId);
        return result is null
            ? $"Error: source {sourceType}/{sourceId:N} was not found in the active project or an allowed direct reference."
            : JsonSerializer.Serialize(result);
    }

    private async Task<string> SearchProjectAsync(WritingCoachContext context, string query, int topK, string[]? sourceTypes, string[]? sourceIds, Guid? containerSourceId, bool lexicalOnly)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        IReadOnlyList<Guid>? parsedIds = null;
        if (sourceIds is { Length: > 0 })
        {
            var ids = new List<Guid>();
            foreach (var value in sourceIds)
                if (!Guid.TryParse(value, out var id)) return $"Error: '{value}' is not a valid source ID.";
                else ids.Add(id);
            parsedIds = ids;
        }
        var result = await projectSearch.SearchAsync(new ProjectSearchRequest(
            context.ProjectId, query.Trim(), Math.Clamp(topK, 1, 30), sourceTypes, parsedIds, containerSourceId, lexicalOnly,
            IncludeReferencedProjects: true));
        return ProjectSearchAgentPayload.SerializeResults(query.Trim(), result);
    }

    private async Task<string> ListReferenceVisualsAsync(WritingCoachContext context)
    {
        var visuals = await referenceVisuals.ListAsync(context.ProjectId);
        return JsonSerializer.Serialize(new
        {
            resultKind = "referenceVisualDiscovery", returnedCount = visuals.Count,
            boundedLimit = ReferenceVisualService.MaximumListResults,
            mayHaveMore = visuals.Count == ReferenceVisualService.MaximumListResults,
            note = "Direct-reference canonical visuals are read-only continuity evidence and cannot be placed or mutated in the active project.", visuals,
        });
    }

    private async Task<string> ReadReferenceVisualAsync(WritingCoachContext context, Guid originProjectId, Guid imageId)
    {
        var visual = await referenceVisuals.ReadAsync(context.ProjectId, originProjectId, imageId, context.VisionReady);
        if (visual is null) return $"Error: image {imageId:N} is not an eligible canonical visual on a direct referenced project.";
        if (visual.DataDelivered) context.QueueReferenceVisual(visual);
        return JsonSerializer.Serialize(new
        {
            visual.OriginProjectId, visual.OriginProjectName, visual.OriginProjectSlug,
            visual.EntityId, visual.EntityType, visual.EntityName, visual.CanonicalReferenceId,
            visual.Label, visual.SortOrder, visual.ImageId, visual.FileName, visual.ContentType,
            visual.PreviewUrl, visual.AltText, visual.Prompt, visual.ImageSource,
            visual.IsReferenced, visual.DataDelivered,
            detailReadArguments = new { originProjectId = visual.OriginProjectId, imageId = visual.ImageId },
        });
    }

    private static string ReadCurrentSection(WritingCoachContext context)
    {
        var title = string.IsNullOrWhiteSpace(context.CurrentSampleTitle)
            ? "Untitled writing sample"
            : context.CurrentSampleTitle.Trim();
        var body = context.CurrentSampleBody ?? string.Empty;

        return JsonSerializer.Serialize(new
        {
            title,
            body,
            isEmpty = string.IsNullOrWhiteSpace(body),
        });
    }

    private async Task<string> ListProjectFactsAsync(WritingCoachContext context)
    {
        var facts = await projectFacts.ListAsync(context.ProjectId);
        var payload = new List<object>();
        foreach (var fact in facts)
        {
            var linkedEntities = new List<object>();
            foreach (var link in fact.LinkedEntities)
            {
                linkedEntities.Add(new
                {
                    link.EdgeType,
                    direction = link.Direction.ToString(),
                    link.EntityId,
                    link.EntityName,
                    link.EntityType,
                    relationContext = await entityRelations.BuildForEntityAsync(context.ProjectId, link.EntityId, EntityRelationOptions),
                });
            }

            payload.Add(new
            {
                fact.Id,
                fact.Key,
                fact.Name,
                fact.Value,
                linkedEntities,
            });
        }

        return JsonSerializer.Serialize(payload);
    }
}

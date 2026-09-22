using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Authoring;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Outline;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Writing;

public sealed class VoiceContext(
    Guid projectId,
    string? currentSampleTitle,
    string? currentSampleBody,
    bool visionReady = false)
{
    private bool _mutated;
    public void MarkMutated() => _mutated = true;
    public bool TakeMutation() { var value = _mutated; _mutated = false; return value; }
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

public sealed class VoiceTools(
    IWritingSampleService samples,
    IEntityService entities,
    VoiceProfileService profiles,
    IAuthoringMutationFence fence,
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

    public IList<AITool> Build(VoiceContext context)
    {
        return new List<AITool>
        {
            AIFunctionFactory.Create((int? pageNumber = null) => ListSamplesAsync(context, pageNumber), "list_writing_samples", "List sample identities and revisions with pagination."),
            AIFunctionFactory.Create((Guid sampleId, int? pageNumber = null) => ReadSampleAsync(context, sampleId, pageNumber), "read_writing_sample", "Read a current sample and revision before editing; follow every page."),
            AIFunctionFactory.Create((string title, string body) => MutateAsync(context, async () => {
                var value = await samples.CreateAsync(context.ProjectId, title, body);
                return (true, new { value.Id, value.Revision });
            }), "create_writing_sample", "Create a project-wide style sample when requested."),
            AIFunctionFactory.Create((Guid sampleId, long expectedRevision, string? title = null, string? body = null) => MutateAsync(context, async () => {
                var before = await samples.GetAsync(sampleId);
                var value = await samples.UpdateAsync(context.ProjectId, sampleId, expectedRevision, title, body);
                return (before?.Revision != value.Revision, new { value.Id, value.Revision });
            }), "update_writing_sample", "Update a sample at its exact read revision. Omitted fields are unchanged."),
            AIFunctionFactory.Create((Guid sampleId, long expectedRevision) => MutateAsync(context, async () => {
                var before = await samples.GetAsync(sampleId);
                await samples.DeleteAsync(context.ProjectId, sampleId, expectedRevision);
                return (before is not null, new { id = sampleId, deleted = true });
            }), "delete_writing_sample", "Delete a sample only when requested, at its exact read revision."),
            AIFunctionFactory.Create((int? pageNumber = null) => ListCharactersAsync(context, pageNumber), "list_characters", "List characters available for voice profiles."),
            AIFunctionFactory.Create((Guid characterId, int? pageNumber = null) => ReadProfileAsync(context, characterId, pageNumber), "read_voice_profile", "Read a character voice profile. Read every page before saving."),
            AIFunctionFactory.Create((Guid characterId, string expectedContent, string content) => MutateAsync(context, async () => {
                var value = await profiles.SaveAsync(context.ProjectId, characterId, expectedContent, content);
                return (expectedContent != content, new { value.Id, saved = true });
            }), "save_voice_profile", "Save free-form dialogue and POV guidance using the exact previously read content. Empty content clears the profile."),
            AIFunctionFactory.Create((string name) => MutateAsync(context, async () => {
                var value = await entities.CreateAsync(context.ProjectId, "Character", name);
                return (true, new { value.Id, value.Name });
            }), "create_character", "Create a character only when the user's request requires it. Search existing characters first."),

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

    private async Task<string> MutateAsync<T>(VoiceContext context, Func<Task<(bool Changed, T Result)>> action) =>
        await fence.ExecuteAsync(new AuthoringFenceRequest(context.ProjectId, [], "Voice edit"), async (_, _) =>
        {
            var result = await action();
            if (result.Changed) context.MarkMutated();
            return JsonSerializer.Serialize(result.Result);
        });

    private static string Page(object detail, string tool, JsonObject args, int? number) =>
        AgentPayloadPaginator.SerializePage(new JsonObject(), JsonSerializer.SerializeToNode(detail), tool, args, number);

    private async Task<string> ListSamplesAsync(VoiceContext context, int? page) =>
        Page((await samples.ListAsync(context.ProjectId)).Select(sample => new { sample.Id, sample.Title, sample.Revision }), "list_writing_samples", new(), page);

    private async Task<string> ReadSampleAsync(VoiceContext context, Guid id, int? page)
    {
        var sample = await samples.GetAsync(id);
        if (sample is null || sample.ProjectId != context.ProjectId) return "Error: sample not found in this project.";
        return Page(new { sample.Id, sample.Title, sample.Body, sample.Revision }, "read_writing_sample", new() { ["sampleId"] = id }, page);
    }

    private async Task<string> ListCharactersAsync(VoiceContext context, int? page) =>
        Page((await entities.ListAsync(context.ProjectId, "Character")).Select(entity => new { entity.Id, entity.Name }), "list_characters", new(), page);

    private async Task<string> ReadProfileAsync(VoiceContext context, Guid id, int? page)
    {
        var entity = await entities.GetAsync(context.ProjectId, id);
        if (entity?.Type != "Character") return "Error: character not found in this project.";
        return Page(new { entity.Id, entity.Name, content = VoiceProfileService.Read(entity) }, "read_voice_profile", new() { ["characterId"] = id }, page);
    }

    private async Task<string> ListSearchSourcesAsync(VoiceContext context, string? query, string[]? sourceTypes, int topK)
    {
        var sources = await projectSearch.ListSourcesAsync(context.ProjectId, query, sourceTypes, Math.Clamp(topK, 1, 30), includeReferencedProjects: true);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ReadProjectSourceAsync(VoiceContext context, string sourceType, Guid sourceId, int? pageNumber, Guid? originProjectId)
    {
        var result = await projectSearch.ReadSourceAsync(context.ProjectId, sourceType, sourceId, pageNumber, originProjectId: originProjectId);
        return result is null
            ? $"Error: source {sourceType}/{sourceId:N} was not found in the active project or an allowed direct reference."
            : JsonSerializer.Serialize(result);
    }

    private async Task<string> SearchProjectAsync(VoiceContext context, string query, int topK, string[]? sourceTypes, string[]? sourceIds, Guid? containerSourceId, bool lexicalOnly)
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

    private async Task<string> ListReferenceVisualsAsync(VoiceContext context)
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

    private async Task<string> ReadReferenceVisualAsync(VoiceContext context, Guid originProjectId, Guid imageId)
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

    private static string ReadCurrentSection(VoiceContext context)
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

    private async Task<string> ListProjectFactsAsync(VoiceContext context)
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

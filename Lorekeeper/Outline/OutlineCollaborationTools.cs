using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Graph;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;
using Lorekeeper.Projects;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Outline;

public enum BookBriefUpdatePolicy
{
    OutlineMaintainer,
    ExplicitUserRequestOnly,
    Disallowed,
}

public enum OutlineToolSurface
{
    Outline,
    Editor,
}

/// <summary>
/// Per-turn context captured by every Outline collaboration tool. <see cref="OnMutated"/>
/// is invoked after every successful mutating tool call so the streaming service can emit
/// an <see cref="OutlineMutated"/> event to refresh the live tree in the UI.
/// </summary>
public sealed class OutlineCollaborationContext(
    Guid projectId,
    Action onMutated,
    bool visionReady = false,
    Action<IEnumerable<EntityVisualContextReference>>? onVisualsQueued = null,
    Action<ReferenceVisualReadResult>? onReferenceVisualQueued = null,
    BookBriefUpdatePolicy bookBriefUpdatePolicy = BookBriefUpdatePolicy.OutlineMaintainer,
    OutlineToolSurface surface = OutlineToolSurface.Outline)
{
    private readonly List<EntityVisualContextReference> _pendingVisuals = [];
    private readonly List<ReferenceVisualReadResult> _pendingReferenceVisuals = [];
    private readonly HashSet<Guid> _imageGenerationJobIds = [];
    public Guid ProjectId { get; } = projectId;
    public Action OnMutated { get; } = onMutated;
    public bool VisionReady { get; } = visionReady;
    public BookBriefUpdatePolicy BookBriefUpdatePolicy { get; } = bookBriefUpdatePolicy;
    public OutlineToolSurface Surface { get; } = surface;
    public IReadOnlyList<Guid> ImageGenerationJobIds => _imageGenerationJobIds.ToList();
    public void TrackImageGenerationJob(Guid jobId) => _imageGenerationJobIds.Add(jobId);
    public void QueueVisuals(IEnumerable<EntityVisualContextReference> visuals)
    {
        var list = visuals.ToList();
        _pendingVisuals.AddRange(list);
        onVisualsQueued?.Invoke(list);
    }
    public IReadOnlyList<EntityVisualContextReference> DrainVisuals()
    {
        var result = _pendingVisuals.ToList();
        _pendingVisuals.Clear();
        return result;
    }
    public void QueueReferenceVisual(ReferenceVisualReadResult visual)
    {
        if (visual.DataDelivered)
        {
            _pendingReferenceVisuals.Add(visual);
            onReferenceVisualQueued?.Invoke(visual);
        }
    }
    public IReadOnlyList<ReferenceVisualReadResult> DrainReferenceVisuals()
    {
        var result = _pendingReferenceVisuals.ToList();
        _pendingReferenceVisuals.Clear();
        return result;
    }
}

/// <summary>
/// Builds the set of <see cref="AITool"/>s exposed to the LLM during an Outline
/// collaboration turn. Every tool closure captures the
/// per-request <see cref="OutlineCollaborationContext"/> so behavior stays project-scoped
/// without ambient state.
/// </summary>
public sealed class OutlineCollaborationTools(
IAppDatabaseOperationFactory database, IActService acts, IChapterService chapters, IEntityService entities, IEntityTypeService entityTypes, IProjectFactService projectFacts, IEntityRelationContextService entityRelations, IProjectSearchService projectSearch, IEntityVisualExampleService entityVisualExamples, IProjectImageService projectImages, IManuscriptService manuscripts, IBookBriefService bookBriefs, IBookFormatGuidanceService formatGuidance, IReferenceVisualService referenceVisuals, IAgentProjectImageWorkflow? imageWorkflow = null)
{
    private const string UnassignedSentinel = "unassigned";
    /// <summary>Canonical entity type for chapter-scoped beats.</summary>
    private const string EventNodeType = "Event";

    private static readonly HashSet<string> EditorSharedToolNames =
    [
        "list_outline", "create_act", "update_act", "delete_act",
        "create_chapter", "update_chapter", "delete_chapter",
        "reorder_acts", "reorder_chapters",
        "list_entity_types", "create_entity", "update_entity", "delete_entity",
        "reorder_entities", "link_entities", "read_book_format_guidance", "update_book_brief",
    ];

    private static readonly HashSet<string> ResearchSharedToolNames =
    [
        "list_entity_types", "list_search_sources", "read_project_source", "search_project",
        "list_reference_visuals", "read_reference_visual",
        "search_entities", "create_entity", "update_entity", "link_entities",
        "list_entity_canonical_references", "attach_entity_canonical_reference",
        "update_entity_canonical_reference", "detach_entity_canonical_reference", "crop_project_image",
    ];

    private static readonly EntityRelationContextOptions EntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    public Task<IList<AITool>> BuildAsync(
        OutlineCollaborationContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = BuildSharedTools(context);
        tools.AddRange([
            AIFunctionFactory.Create(
                method: (ImageGenerationBrief brief, ImageReferenceUse[]? references = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int? minimumDpi = null, string? aspectRatio = null) =>
                    GenerateProjectImageAsync(context, brief, references, altText, quality, outputFormat, outputCompression, minimumDpi, aspectRatio, cancellationToken),
                name: "generate_project_image",
                description: "Generate one unattached project image only when the user explicitly asks to establish or revise an entity's canonical appearance. Omit minimumDpi for the moderate Core Book page raster; when explicitly requested, minimumDpi uses the exact Core Book page as its physical basis, or the largest fitting rectangle for aspectRatio. An infeasible request rejects before dispatch with provider limits and a panel plan. Geometry targets and manuscript placement are unavailable in Outline; multi-image publication coverage requires multiple Figure blocks or a confirmed conversion to a Designed Page. Inspect effectiveDpi and minimumDpiMet before attaching the image."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ImageEditBrief brief, ImageReferenceUse[]? references = null, string? altText = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int? minimumDpi = null, string? aspectRatio = null) =>
                    EditProjectImageAsync(context, sourceImageId, brief, references, altText, quality, outputFormat, outputCompression, minimumDpi, aspectRatio, cancellationToken),
                name: "edit_project_image",
                description: "Edit one project image only when the user explicitly asks to refine an entity's canonical appearance. Omit minimumDpi for the moderate Core Book page raster; an explicit minimumDpi may use an optional aspectRatio and rejects before dispatch when infeasible. Same-aspect up-resolution preserves complete source framing/content while reconstructing credible detail; it does not zoom out, crop, or invent surrounding canvas. Intentional framing expansion is separate outpainting. The output is a new unattached project image; inspect effectiveDpi and minimumDpiMet before attaching it."),

            AIFunctionFactory.Create(
                method: (Guid jobId) => ReadProjectImageJobAsync(context, jobId, wait: false, cancellationToken),
                name: "read_project_image_job",
                description: "Read compact status for an existing canonical-appearance image job without replaying its prompt."),

            AIFunctionFactory.Create(
                method: (Guid jobId) => ReadProjectImageJobAsync(context, jobId, wait: true, cancellationToken),
                name: "wait_project_image_job",
                description: "Reconnect to an existing canonical-appearance image job and wait for its terminal result without replaying the prompt."),

            AIFunctionFactory.Create(
                method: (Guid jobId) => CancelProjectImageJobAsync(context, jobId, cancellationToken),
                name: "cancel_project_image_job",
                description: "Cancel one queued or running canonical-appearance image job owned by this project."),
        ]);
        return Task.FromResult<IList<AITool>>(tools);
    }

    public Task<IList<AITool>> BuildEditorSharedAsync(
        OutlineCollaborationContext context)
        => BuildSharedSubsetAsync(context, EditorSharedToolNames);

    public Task<IList<AITool>> BuildResearchSharedAsync(
        OutlineCollaborationContext context)
        => BuildSharedSubsetAsync(context, ResearchSharedToolNames);

    private Task<IList<AITool>> BuildSharedSubsetAsync(
        OutlineCollaborationContext context,
        IReadOnlySet<string> allowedNames)
    {
        IList<AITool> tools = BuildSharedTools(context)
            .OfType<AIFunction>()
            .Where(tool => allowedNames.Contains(tool.Name))
            .Cast<AITool>()
            .ToList();
        return Task.FromResult(tools);
    }

    private List<AITool> BuildSharedTools(OutlineCollaborationContext context)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Return compact current-project and direct-reference source discovery with origin project provenance and exact read_project_source arguments. References are read-only continuity evidence."),

            AIFunctionFactory.Create(
                method: (string? query = null, bool? canonOnly = null, int page = 1) =>
                    ListIngestedSourcesAsync(context, query, canonOnly, page),
                name: "list_ingested_sources",
                description: "List ingested sources only, in concise pages with canonical-selection status and exact read_project_source arguments. Use canonOnly=true for Book Brief canonical sources; follow nextPageArguments until complete."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null, Guid? originProjectId = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber, originProjectId),
                name: "read_project_source",
                description: "Read one paginated active-project or direct-reference source. Pass the exact originProjectId returned by discovery for referenced continuity evidence; arbitrary foreign project IDs are rejected."),

            AIFunctionFactory.Create(
                method: (
                    string query,
                    int topK = 8,
                    string[]? sourceTypes = null,
                    string[]? sourceIds = null,
                    Guid? containerSourceId = null,
                    bool lexicalOnly = false) =>
                    SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Hybrid keyword + semantic compact discovery across the active project and direct references, with origin provenance and exact origin-qualified read arguments. References are read-only."),

            AIFunctionFactory.Create(
                method: () => ListReferenceVisualsAsync(context),
                name: "list_reference_visuals",
                description: "List bounded canonical entity visuals from direct referenced projects only. Results include origin project/entity/image provenance and are read-only continuity evidence."),

            AIFunctionFactory.Create(
                method: (Guid originProjectId, Guid imageId) => ReadReferenceVisualAsync(context, originProjectId, imageId),
                name: "read_reference_visual",
                description: "Read one canonical entity visual attached to a direct referenced project. Both originProjectId and imageId must come from list_reference_visuals; general-library and active-project images are rejected. Vision-ready providers receive bounded bytes on the next round; this never authorizes placement or mutation."),

            AIFunctionFactory.Create(
                method: () => ListOutlineAsync(context),
                name: "list_outline",
                description: "Read the outline as structured JSON with ids, ordering, projectFacts, chapter beat counts, and current persisted state. The outline text is already in the editor Context Feed; use this for mutations, current-state verification, or missing/insufficient feed context."),

            CreateBookBriefUpdateTool(context),

            AIFunctionFactory.Create(
                method: (Guid[] sourceIds, bool explicitUserRequest = false) =>
                    UpdateBookBriefCanonSourcesAsync(context, sourceIds, explicitUserRequest),
                name: "update_book_brief_canon_sources",
                description: "Replace the Book Brief canonical-source selection with the complete sourceIds list. Use only when the user explicitly requested this selection change and set explicitUserRequest=true. An empty list clears the selection."),

            AIFunctionFactory.Create(
                method: (string title, string synopsis) => CreateActAsync(context, title, synopsis),
                name: "create_act",
                description: "Create a new act at the end of the outline. Returns the new act's id and order."),

            AIFunctionFactory.Create(
                method: (Guid actId, string? title = null, string? synopsis = null) => UpdateActAsync(context, actId, title, synopsis),
                name: "update_act",
                description: "Update an act's title and/or synopsis. Pass null to leave a field unchanged."),

            AIFunctionFactory.Create(
                method: (Guid actId) => DeleteActAsync(context, actId),
                name: "delete_act",
                description: "Delete an act. Any chapters it owned move to the project's unassigned bucket."),

            AIFunctionFactory.Create(
                method: (
                    string title,
                    string synopsis,
                    string? actId = null) =>
                    CreateChapterAsync(context, actId, title, synopsis),
                name: "create_chapter",
                description: "Create a format-neutral structural chapter. Pass an act Guid, or omit actId for the unassigned bucket. Manuscript blocks and page layouts belong to Editor."),

            AIFunctionFactory.Create(
                method: (
                    Guid chapterId,
                    string? title = null,
                    string? synopsis = null,
                    string? actId = null) =>
                    UpdateChapterAsync(context, chapterId, title, synopsis, actId),
                name: "update_chapter",
                description: "Update a chapter title or synopsis, or move it between act buckets. Null leaves a field unchanged; 'unassigned' removes act ownership."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? startLine = null, int? endLine = null) => ReadChapterAsync(context, chapterId, startLine, endLine),
                name: "read_chapter",
                description: "Read a chapter's persisted body with line numbers (0001: ...) only when manuscript text is explicitly relevant to the user's request. This is read-only and does not authorize manuscript synchronization. Pass optional startLine/endLine for a focused range."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => DeleteChapterAsync(context, chapterId),
                name: "delete_chapter",
                description: context.Surface == OutlineToolSurface.Outline
                    ? "Delete an empty structural chapter. Outline cannot delete a chapter containing authored manuscript content; use Editor or the manual UI for that destructive action."
                    : "Delete a chapter, including its manuscript content, when the user directly requests that destructive change."),

            AIFunctionFactory.Create(
                method: (Guid[] orderedIds) => ReorderActsAsync(context, orderedIds),
                name: "reorder_acts",
                description: "Replace the act ordering with the given sequence of act ids. Any acts not in the list keep their relative order at the end."),

            AIFunctionFactory.Create(
                method: (Guid[] orderedIds, string? actId = null) => ReorderChaptersAsync(context, actId, orderedIds),
                name: "reorder_chapters",
                description: "Replace chapter ordering within a single act bucket. Pass actId as a Guid for that act, or omit/null/'unassigned' for the unassigned bucket."),

            // ---- generic entity tools (Characters, Locations, Events/beats, ...) ----

            AIFunctionFactory.Create(
                method: () => ListEntityTypesAsync(context),
                name: "list_entity_types",
                description: "List registered and discovered graph entity types for this project, including structural types such as Project, Act, Chapter, and Event/Beat."),

            AIFunctionFactory.Create(
                method: (string? type = null, int page = 1) => ListEntitiesAsync(context, type, page),
                name: "list_entities",
                description: "Browse concise, explicitly paginated non-structural entities, optionally filtered by type. Each item includes origin, source-evidence count, and exact read_entity arguments. Follow nextPageArguments until complete."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 10, string? type = null, string? parentId = null) => SearchEntitiesAsync(context, query, topK, type, parentId),
                name: "search_entities",
                description: "Compact entity discovery with full IDs, total/returned counts, completeness, labeled previews, and exact read_entity arguments. Results reflect current persisted state."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ReadEntityAsync(context, entityId, pageNumber),
                name: "read_entity",
                description: "Read one explicitly paginated entity with properties, knowledge, all adjacent manual and AutoMention links, bounded relation context, and ordered canonical visual references. Full identity fields and GUIDs are repeated on every page; omit pageNumber for page 1 and follow nextPageArguments. Vision-ready providers receive its image bytes on the next model round."),

            AIFunctionFactory.Create(
                method: (Guid entityId, int? pageNumber = null) => ListEntityLinksAsync(context, entityId, pageNumber),
                name: "list_entity_links",
                description: "List explicitly paginated graph links adjacent to an entity. Full identity fields are repeated on every page; follow nextPageArguments until complete."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityVisualExamplesAsync(context, entityId),
                name: "list_entity_canonical_references",
                description: "List ordered canonical visual references attached to an entity and supply their bytes to a vision-ready provider on the next model round."),

            AIFunctionFactory.Create(
                method: (string type, string name, string? propertiesJson = null, string? parentId = null, int? order = null) =>
                    CreateEntityAsync(context, type, name, propertiesJson, parentId, order),
                name: "create_entity",
                description: "Create a new graph entity. type is the entity category ('Character', 'Location', 'Event' for beats, 'ProjectFact' for rare canonical/global constraints, ...). name is the display name. propertiesJson is a JSON object string for the free-form property bag (e.g. '{\"description\":\"...\", \"role\":\"...\"}') or null/empty for none. Never use ProjectFact for Book Brief fields, rework notes, act/chapter plans, character roles, beats, relationships, or location details. For a justified ProjectFact include key/value properties; it will be parented to the Project automatically. For chapter-scoped beats set type='Event' and parentId=<chapter id> (order is auto-assigned to the end if omitted). If an entity with the same name/key already exists, returns status='existing_match' and its compact identity instead of creating a duplicate. Returns compact identity/current properties; use read_entity for full knowledge and relationships."),

            AIFunctionFactory.Create(
                method: (string entityId, string? name = null, string? propertiesToSetJson = null, string? propertiesToRemoveJson = null) =>
                    UpdateEntityAsync(context, entityId, name, propertiesToSetJson, propertiesToRemoveJson),
                name: "update_entity",
                description: "Update an entity's name and/or properties. Pass null for fields to leave unchanged. propertiesToSetJson is a JSON object string of keys to merge into the existing bag (e.g. '{\"role\":\"protagonist\"}'). propertiesToRemoveJson is a JSON array string of keys to delete (e.g. '[\"role\"]'). Returns compact identity/current properties; use read_entity for full knowledge and relationships."),

            AIFunctionFactory.Create(
                method: (string entityId) => DeleteEntityAsync(context, entityId),
                name: "delete_entity",
                description: "Delete an entity and any edges connected to it. Returns a deleted status plus compact deleted identity."),

            AIFunctionFactory.Create(
                method: (string type, string parentId, string orderedIdsJson) =>
                    ReorderEntitiesAsync(context, type, parentId, orderedIdsJson),
                name: "reorder_entities",
                description: "Replace the ordering of a parent's children of a given type. orderedIdsJson is a JSON array string of entity ids (e.g. '[\"<guid1>\", \"<guid2>\"]') and must contain exactly the parent's current children of that type. Used primarily to reorder beats within a chapter. Returns compact ordered child identities."),

            AIFunctionFactory.Create(
                method: (string fromId, string toId, string edgeType, string? propertiesJson = null) =>
                    LinkEntitiesAsync(context, fromId, toId, edgeType, propertiesJson),
                name: "link_entities",
                description: "Create a typed edge between two entities. propertiesJson is optional JSON metadata. Prefer 'RelevantTo' from every canonical entity to each Chapter it matters to; Editor uses these chapter links for automatic context. Use 'AppearsIn' only for Character -> Event/beat, 'LocatedAt' for Event -> Location, and 'KnownTo' for Character -> Character. Returns link details plus compact endpoints."),

            AIFunctionFactory.Create(
                method: (Guid entityId, Guid imageId, string? label = null) => AttachEntityVisualAsync(context, entityId, imageId, label),
                name: "attach_entity_canonical_reference",
                description: "Attach one isolated, stable appearance or design image to a non-structural entity as a labeled canonical reference. Do not attach an ordinary narrative scene merely because the entity appears in it."),

            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId, string label, int? sortOrder = null) => UpdateEntityVisualAsync(context, canonicalReferenceId, label, sortOrder),
                name: "update_entity_canonical_reference",
                description: "Relabel or reorder an attached entity canonical visual reference."),

            AIFunctionFactory.Create(
                method: (Guid canonicalReferenceId) => DetachEntityVisualAsync(context, canonicalReferenceId),
                name: "detach_entity_canonical_reference",
                description: "Detach an entity canonical visual reference without deleting its image."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ProjectImageCropRegion crop, string? fileName = null, string? altText = null, EntityVisualTarget? entityTarget = null) =>
                    CropProjectImageAsync(context, sourceImageId, crop, fileName, altText, entityTarget),
                name: "crop_project_image",
                description: "Create a non-destructive project-library crop from an existing image using 0-100 percentage coordinates. Inspect the source first or use user-supplied coordinates and describe only the cropped subject in altText. Optionally attach the tight subject-only crop to one entity as its canonical reference; make separate crops for separate entities. Source associations are never inherited."),

        };

        tools.Add(AIFunctionFactory.Create(
            method: (int offset = 0, int limit = 4, bool includePublicationConstraints = false) =>
                ReadBookFormatGuidanceAsync(context, offset, limit, includePublicationConstraints),
            name: "read_book_format_guidance",
            description: "Read compact, paginated genre-aware structural recommendations derived from the current Book Brief. Set includePublicationConstraints=true only when the user explicitly asks how the outline should adapt to publication formats."));
        return tools;
    }

    private async Task<string> ReadBookFormatGuidanceAsync(
        OutlineCollaborationContext context,
        int offset,
        int limit,
        bool includePublicationConstraints)
    {
        var brief = await bookBriefs.GetOrCreateAsync(context.ProjectId);
        var scope = includePublicationConstraints
            ? BookFormatGuidanceScope.StructureAndPublication
            : BookFormatGuidanceScope.StructureOnly;
        var items = formatGuidance.Read(brief, offset, limit, scope);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            offset = Math.Max(0, offset),
            items,
            nextOffset = items.Count == Math.Clamp(limit, 1, 8) ? Math.Max(0, offset) + items.Count : (int?)null,
        });
    }

    private AITool CreateBookBriefUpdateTool(OutlineCollaborationContext context) =>
        context.BookBriefUpdatePolicy switch
        {
            BookBriefUpdatePolicy.OutlineMaintainer => AIFunctionFactory.Create(
                method: (BookBriefPatch patch) => UpdateBookBriefAsync(context, patch, explicitUserRequest: false),
                name: "update_book_brief",
                description: "Directly apply a partial Book Brief patch to live persisted state and return the complete persisted brief. Omit null fields to leave them unchanged. For ordinary updates omit clearFields or pass []; clearFields is only for deliberately removing existing values and accepts only the listed field-name enum values. Review Edits controls whether the completed mutating turn is checkpointed."),
            BookBriefUpdatePolicy.ExplicitUserRequestOnly => AIFunctionFactory.Create(
                method: (BookBriefPatch patch, bool explicitUserRequest = false) => UpdateBookBriefAsync(context, patch, explicitUserRequest),
                name: "update_book_brief",
                description: "Directly apply a partial Book Brief patch to live persisted state only after an explicit user request and return the complete persisted brief. Set explicitUserRequest=true only when the user explicitly requested the change. Omit null fields to leave them unchanged. For ordinary updates omit clearFields or pass []; clearFields only deliberately removes values and accepts the listed field-name enum values. Review Edits controls whether the completed mutating turn is checkpointed."),
            _ => AIFunctionFactory.Create(
                method: () => "Error: this chat is not authorized to change the Book Brief.",
                name: "update_book_brief",
                description: "This chat is not authorized to change the Book Brief."),
        };

    private async Task<string> UpdateBookBriefAsync(
        OutlineCollaborationContext context,
        BookBriefPatch patch,
        bool explicitUserRequest)
    {
        if (context.BookBriefUpdatePolicy == BookBriefUpdatePolicy.Disallowed)
            return "Error: this chat is not authorized to change the Book Brief.";
        if (context.BookBriefUpdatePolicy == BookBriefUpdatePolicy.ExplicitUserRequestOnly && !explicitUserRequest)
            return "Error: Editor Chat may change the Book Brief only after an explicit user request; pass explicitUserRequest=true only when that condition is satisfied.";

        try
        {
            var updated = await bookBriefs.UpdateAsync(context.ProjectId, patch);
            var removedLegacyFacts = context.BookBriefUpdatePolicy == BookBriefUpdatePolicy.OutlineMaintainer
                ? await RemoveEquivalentLegacyBriefFactsAsync(context.ProjectId, patch, updated)
                : [];
            context.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "updated",
                removedEquivalentLegacyFacts = removedLegacyFacts,
                brief = BookBriefPayload(updated),
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<IReadOnlyList<string>> RemoveEquivalentLegacyBriefFactsAsync(
        Guid projectId,
        BookBriefPatch patch,
        BookBrief updated)
    {
        var cleared = (patch.ClearFields ?? []).ToHashSet();
        var writtenValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(BookBriefField field, object? patchValue, object? updatedValue, params string[] aliases)
        {
            if (patchValue is null || cleared.Contains(field) || updatedValue is null)
                return;
            var value = updatedValue.ToString();
            if (string.IsNullOrWhiteSpace(value))
                return;
            foreach (var alias in aliases)
                writtenValues[alias] = value;
        }

        Add(BookBriefField.BookKind, patch.BookKind, updated.BookKind, "bookkind");
        Add(BookBriefField.Premise, patch.Premise, updated.Premise, "premise");
        Add(BookBriefField.Genre, patch.Genre, updated.Genre, "genre");
        Add(BookBriefField.PrimaryThemes, patch.PrimaryThemes, updated.PrimaryThemes, "primarythemes", "themes", "theme");
        Add(BookBriefField.Purpose, patch.Purpose, updated.Purpose, "purpose");
        Add(BookBriefField.CreativeConstraints, patch.CreativeConstraints, updated.CreativeConstraints, "creativeconstraints", "constraints");
        Add(BookBriefField.TargetAudience, patch.TargetAudience, updated.TargetAudience, "targetaudience", "audience");
        Add(BookBriefField.MinimumReaderAge, patch.MinimumReaderAge, updated.MinimumReaderAge, "minimumreaderage", "minreaderage");
        Add(BookBriefField.MaximumReaderAge, patch.MaximumReaderAge, updated.MaximumReaderAge, "maximumreaderage", "maxreaderage");
        Add(BookBriefField.ReadingLevelGuidance, patch.ReadingLevelGuidance, updated.ReadingLevelGuidance, "readinglevelguidance", "readinglevel");
        Add(BookBriefField.TargetWordCount, patch.TargetWordCount, updated.TargetWordCount, "targetwordcount", "wordcount");
        Add(BookBriefField.PointOfView, patch.PointOfView, updated.PointOfView, "pointofview", "pov");
        Add(BookBriefField.Tense, patch.Tense, updated.Tense, "tense");
        Add(BookBriefField.VoiceAndTone, patch.VoiceAndTone, updated.VoiceAndTone, "voiceandtone", "voice", "tone");
        Add(BookBriefField.LanguageLocale, patch.LanguageLocale, updated.LanguageLocale, "languagelocale", "language", "locale");
        Add(BookBriefField.HouseStyle, patch.HouseStyle, updated.HouseStyle, "housestyle");
        Add(BookBriefField.ReadAloudPriority, patch.ReadAloudPriority, updated.ReadAloudPriority, "readaloudpriority", "readaloud");
        Add(BookBriefField.AccessibilityGoals, patch.AccessibilityGoals, updated.AccessibilityGoals, "accessibilitygoals", "accessibility");
        Add(BookBriefField.VisualDirection, patch.VisualDirection, updated.VisualDirection, "visualdirection", "visualstrategy");
        if (writtenValues.Count == 0)
            return [];

        var removed = new List<string>();
        foreach (var fact in await projectFacts.ListAsync(projectId))
        {
            if (!fact.Key.StartsWith("outline.", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = NormalizeLegacyFactName(fact.Key[8..]);
            if (!writtenValues.TryGetValue(suffix, out var writtenValue)
                || !EquivalentBriefValue(fact.Value, writtenValue))
            {
                continue;
            }
            await projectFacts.DeleteAsync(projectId, fact.Id);
            removed.Add(fact.Key);
        }
        return removed;
    }

    private static string NormalizeLegacyFactName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool EquivalentBriefValue(string left, string right) =>
        string.Equals(
            NormalizeLegacyFactName(left.Trim()),
            NormalizeLegacyFactName(right.Trim()),
            StringComparison.Ordinal);

    private async Task<string> ListSearchSourcesAsync(
        OutlineCollaborationContext ctx,
        string? query,
        string[]? sourceTypes,
        int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(ctx.ProjectId, query, sourceTypes, topK, includeReferencedProjects: true);
        return ProjectSearchAgentPayload.SerializeSources(sources);
    }

    private async Task<string> ListReferenceVisualsAsync(OutlineCollaborationContext ctx)
    {
        var visuals = await referenceVisuals.ListAsync(ctx.ProjectId);
        return JsonSerializer.Serialize(new
        {
            resultKind = "referenceVisualDiscovery",
            returnedCount = visuals.Count,
            boundedLimit = ReferenceVisualService.MaximumListResults,
            mayHaveMore = visuals.Count == ReferenceVisualService.MaximumListResults,
            note = "Canonical visuals are read-only continuity evidence from direct references. Use read_reference_visual with the exact originProjectId and imageId; they cannot be attached or placed in the active project.",
            visuals,
        });
    }

    private async Task<string> ReadReferenceVisualAsync(OutlineCollaborationContext ctx, Guid originProjectId, Guid imageId)
    {
        var visual = await referenceVisuals.ReadAsync(ctx.ProjectId, originProjectId, imageId, ctx.VisionReady);
        if (visual is null)
            return $"Error: image {imageId:N} is not an eligible canonical visual on a direct referenced project.";
        if (visual.DataDelivered) ctx.QueueReferenceVisual(visual);
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

    private async Task<string> UpdateBookBriefCanonSourcesAsync(
        OutlineCollaborationContext context,
        Guid[] sourceIds,
        bool explicitUserRequest)
    {
        if (!explicitUserRequest)
            return "Error: canonical-source selection can change only after an explicit user request; set explicitUserRequest=true only when that condition is satisfied.";
        try
        {
            await bookBriefs.ReplaceCanonSourcesAsync(context.ProjectId, sourceIds.Distinct().ToArray());
            var selected = await bookBriefs.ListCanonSourcesAsync(context.ProjectId);
            context.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "updated",
                canonicalSources = selected,
                count = selected.Count,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ListIngestedSourcesAsync(
        OutlineCollaborationContext ctx,
        string? query,
        bool? canonOnly,
        int page)
    {
        const int pageSize = 20;
        page = Math.Max(1, page);
        await using var operation = await database.OpenReadAsync();
        var canonicalIds = (await bookBriefs.ListCanonSourcesAsync(ctx.ProjectId))
            .Select(source => source.SourceId)
            .ToHashSet();
        var sources = await operation.Db.IngestSources
            .AsNoTracking()
            .Where(source => source.ProjectId == ctx.ProjectId)
            .OrderBy(source => source.Title)
            .ThenBy(source => source.CreatedAt)
            .Select(source => new
            {
                source.Id,
                source.Title,
                source.SourceKind,
                source.Description,
                source.Synopsis,
                source.UpdatedAt,
            })
            .ToListAsync();
        var filtered = sources
            .Where(source => string.IsNullOrWhiteSpace(query)
                || source.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || source.SourceKind.Contains(query, StringComparison.OrdinalIgnoreCase)
                || source.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || source.Synopsis.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(source => canonOnly is null || canonicalIds.Contains(source.Id) == canonOnly.Value)
            .ToList();
        var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(source => new
        {
            sourceId = source.Id,
            source.Title,
            source.SourceKind,
            isCanonical = canonicalIds.Contains(source.Id),
            preview = TruncatePropertyValue(string.IsNullOrWhiteSpace(source.Synopsis) ? source.Description : source.Synopsis),
            source.UpdatedAt,
            detailReadTool = "read_project_source",
            detailReadArguments = new { sourceType = ProjectSearchSourceTypes.IngestSource, sourceId = source.Id, pageNumber = 1 },
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            page,
            pageSize,
            total = filtered.Count,
            returned = items.Count,
            isComplete = page * pageSize >= filtered.Count,
            items,
            nextPageArguments = page * pageSize < filtered.Count ? new { query, canonOnly, page = page + 1 } : null,
        });
    }

    private async Task<string> ReadProjectSourceAsync(
        OutlineCollaborationContext ctx,
        string sourceType,
        Guid sourceId,
        int? pageNumber,
        Guid? originProjectId)
    {
        var result = await projectSearch.ReadSourceAsync(ctx.ProjectId, sourceType, sourceId, pageNumber, originProjectId: originProjectId);
        if (result is null)
            return $"Error: source {sourceType}/{sourceId:N} was not found in this project.";
        if (string.Equals(sourceType, ProjectSearchSourceTypes.Entity, StringComparison.OrdinalIgnoreCase))
            await QueueEntityVisualsAsync(ctx, sourceId);
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> ReadEntityAsync(OutlineCollaborationContext ctx, Guid entityId, int? pageNumber)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null) return $"Error: entity {entityId} not found in this project.";
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        var relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, entity.Id, EntityRelationOptions);
        var visuals = await QueueEntityVisualsAsync(ctx, entityId);
        var detail = JsonSerializer.SerializeToNode(new
        {
            origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
            entity.IsIngestCreated,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            sourceEvidence = entity.SourceEvidence,
            links,
            relationContextPreview = RelationContextPreview(entity.Id, EntityRelationOptions, relationContext),
            canonicalVisualReferences = visuals.Select(VisualPayload),
        });
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            detail,
            "read_entity",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private static object RelationContextPreview(Guid entityId, EntityRelationContextOptions options, object relationContext) => new
    {
        isComplete = false,
        note = "This traversal is a bounded orientation preview. All adjacent links are included separately; use list_entity_links and read_entity to continue traversal.",
        bounds = new { options.Depth, options.MaxDirectLinks, options.MaxTraversalPaths, options.MaxLinksPerNode },
        detailReadTool = "list_entity_links",
        detailReadArguments = new { entityId, pageNumber = 1 },
        value = relationContext,
    };

    private async Task<string> ListEntityLinksAsync(OutlineCollaborationContext ctx, Guid entityId, int? pageNumber)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null) return $"Error: entity {entityId} not found in this project.";
        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        return AgentPayloadPaginator.SerializePage(
            AgentPayloadPaginator.EntityIdentity(entity.Id, entity.Type, entity.Name, entity.Order, entity.ParentId),
            JsonSerializer.SerializeToNode(new { links }),
            "list_entity_links",
            new JsonObject { ["entityId"] = entity.Id },
            pageNumber);
    }

    private async Task<string> ListEntityVisualExamplesAsync(OutlineCollaborationContext ctx, Guid entityId)
    {
        if (await entities.GetAsync(ctx.ProjectId, entityId) is null) return $"Error: entity {entityId} not found in this project.";
        return JsonSerializer.Serialize((await QueueEntityVisualsAsync(ctx, entityId)).Select(VisualPayload));
    }

    private async Task<string> AttachEntityVisualAsync(OutlineCollaborationContext ctx, Guid entityId, Guid imageId, string? label)
    {
        try
        {
            var example = await entityVisualExamples.AttachAsync(ctx.ProjectId, entityId, imageId, label, EntityVisualExampleOrigin.Agent);
            ctx.OnMutated();
            await QueueEntityVisualsAsync(ctx, entityId);
            return JsonSerializer.Serialize(VisualPayload(example));
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> UpdateEntityVisualAsync(OutlineCollaborationContext ctx, Guid exampleId, string label, int? sortOrder)
    {
        try
        {
            var example = await entityVisualExamples.UpdateAsync(ctx.ProjectId, exampleId, label, sortOrder);
            ctx.OnMutated();
            return JsonSerializer.Serialize(VisualPayload(example));
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> DetachEntityVisualAsync(OutlineCollaborationContext ctx, Guid exampleId)
    {
        await entityVisualExamples.DetachAsync(ctx.ProjectId, exampleId);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { status = "detached", canonicalReferenceId = exampleId });
    }

    private async Task<string> CropProjectImageAsync(
        OutlineCollaborationContext ctx,
        Guid sourceImageId,
        ProjectImageCropRegion crop,
        string? fileName,
        string? altText,
        EntityVisualTarget? entityTarget)
    {
        try
        {
            var targetValidation = await entityVisualExamples.ValidateTargetsAsync(
                ctx.ProjectId,
                entityTarget is null ? null : [entityTarget]);
            if (!targetValidation.IsValid)
                return $"Error: {targetValidation.Error} Use a grounded entity id or omit entityTarget.";

            var image = await projectImages.CropAsync(ctx.ProjectId, sourceImageId, new ProjectImageCropRequest(
                crop,
                fileName?.Trim() ?? string.Empty,
                altText?.Trim() ?? string.Empty));
            object? canonicalReference = null;
            if (targetValidation.Targets is [var target])
            {
                var example = await entityVisualExamples.AttachAsync(
                    ctx.ProjectId,
                    target.EntityId,
                    image.Id,
                    target.Label,
                    EntityVisualExampleOrigin.Agent);
                canonicalReference = VisualPayload(example);
            }

            ctx.QueueVisuals([
                new EntityVisualContextReference(
                    image.Id,
                    EntityId: null,
                    EntityType: string.Empty,
                    EntityName: string.Empty,
                    Label: string.IsNullOrWhiteSpace(image.AltText) ? image.FileName : image.AltText,
                    SortOrder: 0,
                    image.FileName,
                    image.AltText,
                    image.Prompt,
                    IsExplicitImage: true,
                    ImageSource: image.Source),
            ]);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "cropped",
                sourceImageId,
                image = new { image.Id, image.FileName, image.ContentType, image.PreviewUrl, image.AltText, image.Source },
                canonicalReference,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> QueueEntityVisualsAsync(OutlineCollaborationContext ctx, Guid entityId)
    {
        var examples = await entityVisualExamples.ListForEntityAsync(ctx.ProjectId, entityId);
        ctx.QueueVisuals(examples.Select(EntityVisualContextService.ToReference));
        return examples;
    }

    private static object VisualPayload(EntityVisualExampleView example) => new
    {
        example.Id,
        example.EntityId,
        example.Label,
        example.SortOrder,
        example.Origin,
        image = new { example.Image.Id, example.Image.FileName, example.Image.AltText, example.Image.Prompt, example.Image.PreviewUrl },
    };

    private async Task<string> SearchProjectAsync(
        OutlineCollaborationContext ctx,
        string query,
        int topK,
        string[]? sourceTypes,
        string[]? sourceIds,
        Guid? containerSourceId,
        bool lexicalOnly)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        var parsedSourceIds = ParseSourceIds(sourceIds, out var parseError);
        if (parseError is not null) return parseError;

        var results = await projectSearch.SearchAsync(new ProjectSearchRequest(
            ctx.ProjectId,
            query.Trim(),
            Math.Clamp(topK, 1, 30),
            sourceTypes,
            parsedSourceIds,
            containerSourceId,
            lexicalOnly,
            IncludeReferencedProjects: true));

        return ProjectSearchAgentPayload.SerializeResults(query.Trim(), results);
    }

    // ---- list ------------------------------------------------------------

    private async Task<string> ListOutlineAsync(OutlineCollaborationContext ctx)
    {
        var actList = await acts.ListAsync(ctx.ProjectId);
        var allChapters = await chapters.ListAsync(ctx.ProjectId);
        var byAct = allChapters.Where(c => c.ActId is not null)
                               .GroupBy(c => c.ActId!.Value)
                               .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order).ToList());
        var unassigned = allChapters.Where(c => c.ActId is null).OrderBy(c => c.Order).ToList();

        var chapterDetails = new Dictionary<Guid, object>();
        foreach (var c in allChapters)
        {
            var beats = await entities.ListAsync(ctx.ProjectId, EventNodeType, c.Id);
            var beatPayloads = new List<object>();
            foreach (var beat in beats.OrderBy(beat => beat.Order))
            {
                var beatLinks = await entities.ListLinksAsync(ctx.ProjectId, beat.Id);
                beatPayloads.Add(new
                {
                    id = beat.Id,
                    order = beat.Order,
                    name = beat.Name,
                    summary = beat.Summary,
                    attachedEntities = beatLinks
                        .Where(link => !string.Equals(link.EdgeType, EntityService.HasChildEdgeType, StringComparison.OrdinalIgnoreCase))
                        .Select(link => new { relationship = link.EdgeType, id = link.OtherEntityId, type = link.OtherEntityType, name = link.OtherEntityName }),
                });
            }
            var chapterEntity = await entities.GetAsync(ctx.ProjectId, c.Id);
            var relevantEntities = chapterEntity is null
                ? []
                : (await entities.ListLinksAsync(ctx.ProjectId, c.Id))
                    .Where(link => string.Equals(link.EdgeType, EntityService.RelevantToEdgeType, StringComparison.OrdinalIgnoreCase))
                    .Select(link => new { id = link.OtherEntityId, type = link.OtherEntityType, name = link.OtherEntityName })
                    .Cast<object>()
                    .ToList();
            chapterDetails[c.Id] = new
            {
                relevantEntities,
                beats = beatPayloads,
            };
        }
        var facts = await projectFacts.ListAsync(ctx.ProjectId);
        var factPayloads = new List<object>();
        foreach (var fact in facts)
            factPayloads.Add(ProjectFactPayload(fact));

        object ProjectChapter(Chapter c) => new
        {
            id = c.Id,
            order = c.Order,
            title = c.Title,
            synopsis = c.Synopsis,
            detail = chapterDetails[c.Id],
        };

        var payload = new
        {
            projectFacts = factPayloads,
            acts = actList.Select(a => new
            {
                id = a.Id,
                order = a.Order,
                title = a.Title,
                synopsis = a.Synopsis,
                chapters = (byAct.TryGetValue(a.Id, out var list) ? list : []).Select(ProjectChapter),
            }),
            unassigned = unassigned.Select(ProjectChapter),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    // ---- act mutations ---------------------------------------------------

    private async Task<string> CreateActAsync(
        OutlineCollaborationContext ctx,
        [Description("Short act title (≤ 8 words).")] string title,
        [Description("1–2 sentence summary of what this act covers.")] string synopsis)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";
        var act = await acts.CreateAsync(ctx.ProjectId, title.Trim(), synopsis?.Trim());
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = act.Id, order = act.Order, title = act.Title, synopsis = act.Synopsis });
    }

    private async Task<string> UpdateActAsync(
        OutlineCollaborationContext ctx,
        Guid actId,
        string? title,
        string? synopsis)
    {
        var existing = await acts.GetAsync(actId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: act {actId} not found in this project.";

        var updated = await acts.UpdateAsync(actId, title?.Trim(), synopsis?.Trim());
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { id = updated.Id, title = updated.Title, synopsis = updated.Synopsis });
    }

    private async Task<string> DeleteActAsync(OutlineCollaborationContext ctx, Guid actId)
    {
        var existing = await acts.GetAsync(actId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: act {actId} not found in this project.";

        await acts.DeleteAsync(actId);
        ctx.OnMutated();
        return $"Deleted act {actId}. Owned chapters were moved to the unassigned bucket.";
    }

    // ---- chapter mutations -----------------------------------------------

    private async Task<string> CreateChapterAsync(
        OutlineCollaborationContext ctx,
        string? actId,
        string title,
        string synopsis)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Error: title is required.";
        var (resolvedActId, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
        if (error is not null) return error;

        var ch = await chapters.CreateAsync(ctx.ProjectId, resolvedActId, title.Trim(), synopsis?.Trim());

        ctx.OnMutated();
        return JsonSerializer.Serialize(ChapterPayload(ch));
    }

    private async Task<string> UpdateChapterAsync(
        OutlineCollaborationContext ctx,
        Guid chapterId,
        string? title,
        string? synopsis,
        string? actId)
    {
        ChapterActAssignment? assignment = null;
        if (actId is not null)
        {
            var (resolved, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
            if (error is not null) return error;
            assignment = new ChapterActAssignment(resolved);
        }

        var existing = await chapters.GetAsync(chapterId);
        if (existing is null || existing.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var updated = await chapters.UpdateAsync(chapterId, title?.Trim(), synopsis?.Trim(), assignment);

        ctx.OnMutated();
        return JsonSerializer.Serialize(ChapterPayload(updated));
    }

    private async Task<string> ReadChapterAsync(OutlineCollaborationContext ctx, Guid chapterId, int? startLine, int? endLine)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var plainText = (await manuscripts.GetManuscriptAsync(EditorContentTarget.Core, chapter.Id))?.PlainText ?? chapter.PlainText;
        var rangeError = FormatLineRange(plainText, startLine, endLine, out var numbered, out var rangeLabel);
        if (rangeError is not null)
            return rangeError;

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(chapter.Title);
        if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
            sb.Append("Synopsis: ").AppendLine(chapter.Synopsis.Trim());
        if (rangeLabel is not null)
            sb.Append("Range: ").AppendLine(rangeLabel);
        sb.AppendLine();
        sb.Append(numbered.Length == 0 ? "(empty)" : numbered);
        return sb.ToString();
    }

    private async Task<string> GenerateProjectImageAsync(
        OutlineCollaborationContext context,
        ImageGenerationBrief brief,
        ImageReferenceUse[]? references,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int? minimumDpi,
        string? aspectRatio,
        CancellationToken cancellationToken)
    {
        if (imageWorkflow is null)
            return JsonSerializer.Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image generation is unavailable." });
        try
        {
            var result = await imageWorkflow.GenerateAsync(
                context.ProjectId,
                brief,
                references,
                geometryGuidance: MinimumDpiTarget(minimumDpi, aspectRatio),
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Outline image",
                context.TrackImageGenerationJob,
                cancellationToken);
            return ImageResult(context, result);
        }
        catch (MinimumDpiUnachievableException ex)
        {
            return SerializeMinimumDpiRejection(ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "GENERATION_REJECTED", summary = ex.Message });
        }
    }

    private async Task<string> EditProjectImageAsync(
        OutlineCollaborationContext context,
        Guid sourceImageId,
        ImageEditBrief brief,
        ImageReferenceUse[]? references,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int? minimumDpi,
        string? aspectRatio,
        CancellationToken cancellationToken)
    {
        if (imageWorkflow is null)
            return JsonSerializer.Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image editing is unavailable." });
        try
        {
            var result = await imageWorkflow.EditAsync(
                context.ProjectId,
                sourceImageId,
                brief,
                null,
                references,
                geometryGuidance: MinimumDpiTarget(minimumDpi, aspectRatio),
                altText,
                quality,
                outputFormat,
                outputCompression,
                "Outline image edit",
                context.TrackImageGenerationJob,
                cancellationToken);
            return ImageResult(context, result);
        }
        catch (MinimumDpiUnachievableException ex)
        {
            return SerializeMinimumDpiRejection(ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return JsonSerializer.Serialize(new { ok = false, code = "EDIT_REJECTED", summary = ex.Message });
        }
    }

    private async Task<string> ReadProjectImageJobAsync(
        OutlineCollaborationContext context,
        Guid jobId,
        bool wait,
        CancellationToken cancellationToken)
    {
        if (imageWorkflow is null)
            return JsonSerializer.Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image generation is unavailable." });
        var result = wait
            ? await imageWorkflow.WaitAsync(context.ProjectId, jobId, context.TrackImageGenerationJob, cancellationToken)
            : await imageWorkflow.ReadAsync(context.ProjectId, jobId, cancellationToken);
        return result is null
            ? JsonSerializer.Serialize(new { ok = false, code = "NOT_FOUND", jobId, summary = "Image job was not found in this project." })
            : ImageResult(context, result);
    }

    private async Task<string> CancelProjectImageJobAsync(
        OutlineCollaborationContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (imageWorkflow is null)
            return JsonSerializer.Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", summary = "Image generation is unavailable." });
        await imageWorkflow.CancelAsync(context.ProjectId, jobId, cancellationToken);
        return JsonSerializer.Serialize(new { ok = true, jobId, status = "cancelled", summary = "Image job cancelled; no image was placed." });
    }

    private static ImageGenerationTarget? MinimumDpiTarget(int? minimumDpi, string? aspectRatio)
    {
        if (minimumDpi is null && string.IsNullOrWhiteSpace(aspectRatio))
            return null;
        return new ImageGenerationTarget
        {
            MinimumDpi = minimumDpi,
            AspectRatio = aspectRatio?.Trim() ?? string.Empty,
        };
    }

    private static string SerializeMinimumDpiRejection(MinimumDpiUnachievableException exception) =>
        JsonSerializer.Serialize(new
        {
            ok = false,
            code = MinimumDpiUnachievableException.Code,
            stage = exception.Stage,
            physicalDimensions = new
            {
                widthInches = exception.Resolution.WidthInches,
                heightInches = exception.Resolution.HeightInches,
            },
            requestedMinimumDpi = exception.RequestedMinimumDpi,
            requiredRaster = new { widthPixels = exception.RequiredRaster.Width, heightPixels = exception.RequiredRaster.Height, size = exception.RequiredRaster.Size },
            largestCompatibleRaster = exception.MaximumRaster is null
                ? null
                : new { widthPixels = (int?)exception.MaximumRaster.Width, heightPixels = (int?)exception.MaximumRaster.Height, size = exception.MaximumRaster.Size },
            maximumAchievableDpi = Math.Round(exception.MaximumAchievableDpi, 1),
            bindingProviderLimits = exception.Resolution.BindingProviderLimits,
            providerConstraints = new
            {
                sizeMultiple = LayoutImageSizeResolver.SizeMultiple,
                minimumPixels = LayoutImageSizeResolver.MinimumPixels,
                maximumPixels = LayoutImageSizeResolver.MaximumPixels,
                maximumEdgePixels = LayoutImageSizeResolver.MaximumEdge,
                minimumAspectRatio = 1d / LayoutImageSizeResolver.MaximumAspectRatio,
                maximumAspectRatio = LayoutImageSizeResolver.MaximumAspectRatio,
            },
            splitSuggestions = exception.SplitSuggestions.Select(suggestion => new
            {
                rows = suggestion.Rows,
                columns = suggestion.Columns,
                panelCount = suggestion.PanelCount,
                panels = suggestion.Panels.Select(panel => new
                {
                    panel.Index,
                    panel.XPercent,
                    panel.YPercent,
                    panel.WidthPercent,
                    panel.HeightPercent,
                    panel.WidthInches,
                    panel.HeightInches,
                    panel.AspectRatio,
                    raster = panel.Raster.Size,
                    effectiveDpi = Math.Round(panel.EffectiveDpi, 1),
                }),
            }),
            partitioningSupported = false,
            summary = exception.Message,
            recovery = "One Figure block holds one image. Use multiple Figure blocks, or confirm a structural change to a Designed Page before using a multi-image layout; treat independently generated images as deliberate panels rather than a seamless panorama.",
        });

    private static string ImageResult(OutlineCollaborationContext context, AgentProjectImageResult result)
    {
        foreach (var image in result.Images)
        {
            context.QueueVisuals([new EntityVisualContextReference(
                image.Id,
                null,
                "ProjectImage",
                image.FileName,
                "generated project image",
                0,
                image.FileName,
                image.AltText,
                image.Prompt,
                IsExplicitImage: true,
                ImageSource: image.Source)]);
        }
        if (result.Images.Count > 0)
            context.OnMutated();
        return JsonSerializer.Serialize(new
        {
            ok = result.Succeeded,
            jobId = result.JobId,
            status = result.Status,
            targetAspect = result.TargetAspect,
            requestedRaster = result.RequestedRaster,
            requestedMinimumDpi = result.RequestedMinimumDpi,
            minimumDpiMet = result.MinimumDpiMet,
            warningCodes = result.WarningCodes ?? [],
            outputImageIds = result.Images.Select(image => image.Id),
            images = result.Outputs.Select(output => new { output.Image.Id, output.Image.FileName, output.Image.ContentType, output.Width, output.Height, output.ActualRaster, output.RasterMatched, output.AspectMatched, effectiveDpi = output.EffectiveDpi is { } dpi ? (double?)Math.Round(dpi, 1) : null, requestedMinimumDpi = output.RequestedMinimumDpi, minimumDpiMet = output.MinimumDpiMet, warningCodes = output.WarningCodes ?? [], output.Image.PreviewUrl }),
            attached = false,
            diagnosticCounts = new { errors = result.Diagnostics.Count, warnings = result.Outputs.Count(output => !output.RasterMatched || !output.AspectMatched || output.MinimumDpiMet == false) },
            diagnostics = result.Diagnostics.Take(3),
            warnings = result.Outputs.Where(output => !output.RasterMatched || !output.AspectMatched || output.MinimumDpiMet == false).Select(output => new
            {
                code = output.MinimumDpiMet == false
                    ? "MINIMUM_DPI_NOT_MET"
                    : !output.RasterMatched && !output.AspectMatched
                        ? "PROVIDER_IMAGE_RASTER_AND_ASPECT_MISMATCH"
                        : !output.RasterMatched ? "PROVIDER_IMAGE_RASTER_MISMATCH" : "IMAGE_ASPECT_MISMATCH",
                message = output.MinimumDpiMet == false
                    ? $"Provider output {output.ActualRaster} achieved only {output.EffectiveDpi:0.0#} effective DPI, below the requested {output.RequestedMinimumDpi:0.0#}. Keep this unattached and do not describe it as publication-compliant."
                    : $"Provider output {output.ActualRaster} did not satisfy the requested raster or target aspect. Inspect before reporting it as achieved.",
            }),
            summary = result.Summary,
            nextAction = result.MinimumDpiMet == false
                ? "Inspect the returned project image, but keep it unattached and do not describe it as publication-compliant."
                : result.Succeeded
                    ? "Inspect the returned project image, then attach its ID as the intended entity's canonical reference before completing the request."
                : null,
        });
    }

    private async Task<string> DeleteChapterAsync(OutlineCollaborationContext ctx, Guid chapterId)
    {
        var existing = await chapters.GetAsync(chapterId);
        if (existing is not null && existing.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        if (ctx.Surface == OutlineToolSurface.Outline
            && existing is not null
            && (HasMeaningfulManuscriptContent(existing.Manuscript)
                || !string.IsNullOrWhiteSpace(existing.PlainText)))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                code = "CHAPTER_HAS_MANUSCRIPT",
                targetId = chapterId,
                summary = "Outline can delete only an empty structural chapter.",
                recovery = "Use Editor or the manual Outline UI to delete a chapter containing authored manuscript content.",
            });
        }

        if (existing is null)
            return $"Error: chapter {chapterId} not found in this project.";

        await chapters.DeleteAsync(chapterId);
        ctx.OnMutated();
        return $"Deleted chapter {chapterId}.";
    }

    // ---- reorders --------------------------------------------------------

    private async Task<string> ReorderActsAsync(OutlineCollaborationContext ctx, Guid[] orderedIds)
    {
        if (orderedIds is null || orderedIds.Length == 0) return "Error: orderedIds is required.";

        var existing = await acts.ListAsync(ctx.ProjectId);
        var existingIds = existing.Select(a => a.Id).ToHashSet();
        var unknown = orderedIds.Where(id => !existingIds.Contains(id)).ToList();
        if (unknown.Count > 0) return $"Error: unknown act ids: {string.Join(", ", unknown)}";

        // Append any acts the model omitted, preserving their current relative order.
        var final = orderedIds.ToList();
        foreach (var a in existing)
            if (!orderedIds.Contains(a.Id)) final.Add(a.Id);

        await acts.ReorderAsync(ctx.ProjectId, final);
        ctx.OnMutated();
        return $"Reordered {final.Count} acts.";
    }

    private async Task<string> ReorderChaptersAsync(OutlineCollaborationContext ctx, string? actId, Guid[] orderedIds)
    {
        if (orderedIds is null || orderedIds.Length == 0) return "Error: orderedIds is required.";

        var (bucket, error) = await ResolveActAsync(ctx, actId, allowUnassigned: true);
        if (error is not null) return error;

        var bucketChapters = (await chapters.ListAsync(ctx.ProjectId))
            .Where(c => c.ActId == bucket)
            .ToList();
        var bucketIds = bucketChapters.Select(c => c.Id).ToHashSet();
        var unknown = orderedIds.Where(id => !bucketIds.Contains(id)).ToList();
        if (unknown.Count > 0) return $"Error: chapter ids not in target bucket: {string.Join(", ", unknown)}";

        var final = orderedIds.ToList();
        foreach (var c in bucketChapters)
            if (!orderedIds.Contains(c.Id)) final.Add(c.Id);

        await chapters.ReorderAsync(ctx.ProjectId, bucket, final);
        ctx.OnMutated();
        return $"Reordered {final.Count} chapters in bucket {(bucket is null ? "unassigned" : bucket.ToString())}.";
    }

    // ---- entity tools ----------------------------------------------------

    private async Task<string> ListEntityTypesAsync(OutlineCollaborationContext ctx)
    {
        var list = await entityTypes.ListAsync(ctx.ProjectId, includeStructural: true);
        return JsonSerializer.Serialize(list.Select(t => new
        {
            type = t.Type,
            singular = t.SingularLabel,
            plural = t.PluralLabel,
            isStructural = t.IsStructural,
            isChapterScoped = t.IsChapterScoped,
            defaultProperties = t.DefaultProperties,
        }));
    }

    private async Task<string> ListEntitiesAsync(OutlineCollaborationContext ctx, string? type, int page)
    {
        const int pageSize = 20;
        page = Math.Max(1, page);
        var typeNames = await SearchableTypeNamesAsync(ctx.ProjectId, type);
        var all = new List<StoryEntity>();
        foreach (var typeName in typeNames)
            all.AddRange(await entities.ListAsync(ctx.ProjectId, typeName));
        var ordered = all.OrderBy(entity => entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(entity => new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
            entity.IsIngestCreated,
            sourceEvidenceCount = entity.SourceEvidence.Count,
            detailReadTool = "read_entity",
            detailReadArguments = new { entityId = entity.Id, pageNumber = 1 },
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            page,
            pageSize,
            total = ordered.Count,
            returned = items.Count,
            isComplete = page * pageSize >= ordered.Count,
            items,
            nextPageArguments = page * pageSize < ordered.Count ? new { type, page = page + 1 } : null,
        });
    }

    private async Task<string> SearchEntitiesAsync(OutlineCollaborationContext ctx, string query, int topK, string? type, string? parentId)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var p)) return $"Error: parentId '{parentId}' is not a valid Guid.";
            parent = p;
        }

        var searchTerms = SearchTerms(query);
        var typeNames = await SearchableTypeNamesAsync(ctx.ProjectId, type);
        var matches = new List<(StoryEntity Entity, int Score)>();
        foreach (var typeName in typeNames)
        {
            var list = await entities.ListAsync(ctx.ProjectId, typeName, parent);
            matches.AddRange(list
                .Select(entity => (Entity: entity, Score: SearchScore(entity, query, searchTerms)))
                .Where(match => match.Score > 0));
        }

        var selected = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Entity.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Entity.Name, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();
        var visuals = await entityVisualExamples.ListForEntitiesAsync(ctx.ProjectId, selected.Select(match => match.Entity.Id).ToList());
        var payload = selected.Select(match => CompactEntitySearchPayload(
            match.Entity, match.Score, visuals.GetValueOrDefault(match.Entity.Id, [])));

        return AgentPayloadPaginator.SerializeCompactDiscovery(query.Trim(), topK, matches.Count, payload, "read_entity");
    }

    private async Task<IReadOnlyList<string>> SearchableTypeNamesAsync(Guid projectId, string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
            return [type.Trim()];

        var list = await entityTypes.ListAsync(projectId, includeStructural: true);
        return list
            .Where(typeDefinition => IsSearchableEntityType(typeDefinition.Type))
            .Select(typeDefinition => typeDefinition.Type)
            .ToList();
    }

    private async Task<string> CreateEntityAsync(
        OutlineCollaborationContext ctx,
        string type,
        string name,
        string? propertiesJson,
        string? parentId,
        int? order)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (string.IsNullOrWhiteSpace(name)) return "Error: name is required.";
        var trimmedType = type.Trim();

        Dictionary<string, string?>? properties;
        try { properties = ParsePropertiesJson(propertiesJson); }
        catch (Exception ex) { return $"Error: propertiesJson is not a valid JSON object: {ex.Message}"; }

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var p)) return $"Error: parentId '{parentId}' is not a valid Guid.";
            // For Event (beats) the parent must be a chapter we know about. Validate up front so
            // the chat surface gets a clear error instead of lazily upserting a phantom node.
            if (string.Equals(type.Trim(), EventNodeType, StringComparison.OrdinalIgnoreCase))
            {
                var chapter = await chapters.GetAsync(p);
                if (chapter is null || chapter.ProjectId != ctx.ProjectId)
                    return $"Error: chapter {p} not found in this project. Beats (type='Event') require a chapter parentId.";
            }
            parent = p;
        }

        if (string.Equals(trimmedType, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
        {
            parent ??= ctx.ProjectId;
            properties ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            properties.TryAdd("key", name.Trim());
            properties.TryAdd("value", string.Empty);
        }

        var duplicate = await FindDuplicateForCreateAsync(ctx, trimmedType, name, properties, parent);
        if (duplicate is not null)
            return await DuplicateEntityResultAsync(ctx.ProjectId, trimmedType, duplicate);

        try
        {
            var created = await entities.CreateAsync(ctx.ProjectId, trimmedType, name.Trim(), properties, parent, order);
            ctx.OnMutated();
            return JsonSerializer.Serialize(EntityMutationPayload(created));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static object ProjectFactPayload(ProjectFact fact) => new
    {
        id = fact.Id,
        key = fact.Key,
        name = fact.Name,
        value = fact.Value,
        linkedEntities = fact.LinkedEntities.Select(link => new
        {
            edgeType = link.EdgeType,
            direction = link.Direction.ToString(),
            entityId = link.EntityId,
            name = link.EntityName,
            type = link.EntityType,
        }),
    };

    private async Task<StoryEntity?> FindDuplicateForCreateAsync(
        OutlineCollaborationContext ctx,
        string type,
        string name,
        IReadOnlyDictionary<string, string?>? properties,
        Guid? parentId)
    {
        var candidates = parentId is not null && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
            ? await entities.ListAsync(ctx.ProjectId, type, parentId)
            : await entities.ListAsync(ctx.ProjectId, type);

        if (string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase))
        {
            var requestedKey = NormalizeForComparison(ReadProperty(properties, "key") ?? name);
            return candidates.FirstOrDefault(candidate =>
                NormalizeForComparison(ReadProperty(candidate.Properties, "key") ?? candidate.Name) == requestedKey);
        }

        var requestedName = NormalizeForComparison(name);
        return candidates.FirstOrDefault(candidate => NormalizeForComparison(candidate.Name) == requestedName);
    }

    private Task<string> DuplicateEntityResultAsync(Guid projectId, string requestedType, StoryEntity duplicate) =>
        Task.FromResult(JsonSerializer.Serialize(new
        {
            status = "existing_match",
            message = $"No new {requestedType} was created because an existing {duplicate.Type} with the same name or key already exists. Use update_entity or link_entities for the existing entity, or create a more distinctly named entity if this is a separate story subject.",
            existing = EntityMutationPayload(duplicate),
        }));

    private static object EntityMutationPayload(StoryEntity entity) => new
    {
        id = entity.Id,
        type = entity.Type,
        name = entity.Name,
        order = entity.Order,
        parentId = entity.ParentId,
        properties = entity.Properties,
        origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
        entity.IsIngestCreated,
        sourceEvidence = entity.SourceEvidence,
    };

    private static string? ReadProperty(IReadOnlyDictionary<string, string?>? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) ? value : null;

    private async Task<string> UpdateEntityAsync(
        OutlineCollaborationContext ctx,
        string entityId,
        string? name,
        string? propertiesToSetJson,
        string? propertiesToRemoveJson)
    {
        if (!Guid.TryParse(entityId, out var id)) return $"Error: entityId '{entityId}' is not a valid Guid.";

        Dictionary<string, string?>? propertiesToSet;
        try { propertiesToSet = ParsePropertiesJson(propertiesToSetJson); }
        catch (Exception ex) { return $"Error: propertiesToSetJson is not a valid JSON object: {ex.Message}"; }

        string[]? propertiesToRemove;
        try { propertiesToRemove = ParseStringArrayJson(propertiesToRemoveJson); }
        catch (Exception ex) { return $"Error: propertiesToRemoveJson is not a valid JSON array of strings: {ex.Message}"; }

        try
        {
            var updated = await entities.UpdateAsync(ctx.ProjectId, id, name?.Trim(), propertiesToSet, propertiesToRemove);
            ctx.OnMutated();
            return JsonSerializer.Serialize(EntityMutationPayload(updated));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> DeleteEntityAsync(OutlineCollaborationContext ctx, string entityId)
    {
        if (!Guid.TryParse(entityId, out var id)) return $"Error: entityId '{entityId}' is not a valid Guid.";
        try
        {
            var existing = await entities.GetAsync(ctx.ProjectId, id);
            if (existing is null)
                return $"Error: entity {id} not found in this project.";

            var deleted = EntityMutationPayload(existing);
            await entities.DeleteAsync(ctx.ProjectId, id);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "deleted",
                deleted,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ReorderEntitiesAsync(
        OutlineCollaborationContext ctx,
        string type,
        string parentId,
        string orderedIdsJson)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Error: type is required.";
        if (!Guid.TryParse(parentId, out var parent)) return $"Error: parentId '{parentId}' is not a valid Guid.";

        string[]? orderedIds;
        try { orderedIds = ParseStringArrayJson(orderedIdsJson); }
        catch (Exception ex) { return $"Error: orderedIdsJson is not a valid JSON array of strings: {ex.Message}"; }
        if (orderedIds is null || orderedIds.Length == 0) return "Error: orderedIdsJson is required.";

        var parsed = new List<Guid>(orderedIds.Length);
        foreach (var s in orderedIds)
        {
            if (!Guid.TryParse(s, out var g)) return $"Error: orderedIdsJson contains invalid Guid '{s}'.";
            parsed.Add(g);
        }

        try
        {
            var trimmedType = type.Trim();
            await entities.ReorderAsync(ctx.ProjectId, trimmedType, parent, parsed);
            var orderedEntities = new List<object>();
            foreach (var id in parsed)
            {
                var entity = await entities.GetAsync(ctx.ProjectId, id);
                if (entity is not null)
                    orderedEntities.Add(EntityMutationPayload(entity));
            }
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "reordered",
                type = trimmedType,
                parentId = parent,
                orderedIds = parsed,
                entities = orderedEntities,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> LinkEntitiesAsync(
        OutlineCollaborationContext ctx,
        string fromId,
        string toId,
        string edgeType,
        string? propertiesJson)
    {
        if (!Guid.TryParse(fromId, out var from)) return $"Error: fromId '{fromId}' is not a valid Guid.";
        if (!Guid.TryParse(toId, out var to)) return $"Error: toId '{toId}' is not a valid Guid.";
        if (string.IsNullOrWhiteSpace(edgeType)) return "Error: edgeType is required.";

        Dictionary<string, string?>? properties;
        try { properties = ParsePropertiesJson(propertiesJson); }
        catch (Exception ex) { return $"Error: propertiesJson is not a valid JSON object: {ex.Message}"; }

        try
        {
            var trimmedEdgeType = edgeType.Trim();
            await entities.LinkAsync(ctx.ProjectId, from, to, trimmedEdgeType, properties);
            var fromEntity = await entities.GetAsync(ctx.ProjectId, from);
            var toEntity = await entities.GetAsync(ctx.ProjectId, to);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "linked",
                link = new
                {
                    fromId = from,
                    toId = to,
                    edgeType = trimmedEdgeType,
                    properties = properties ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
                },
                from = fromEntity is null ? null : OutlineMutationPayloads.Endpoint(fromEntity.Id, fromEntity.Type, fromEntity.Name),
                to = toEntity is null ? null : OutlineMutationPayloads.Endpoint(toEntity.Id, toEntity.Type, toEntity.Name),
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Parses a JSON object string like <c>{"k":"v","k2":null}</c> into a string?-valued dict.
    /// Returns null when the input is null/blank. Throws on malformed JSON or non-object roots.
    /// </summary>
    private static Dictionary<string, string?>? ParsePropertiesJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("expected a JSON object at the root.");
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => prop.Value.GetRawText(),
            };
        }
        return dict;
    }

    /// <summary>
    /// Parses a JSON array string of strings like <c>["a","b"]</c>. Returns null when input is
    /// null/blank. Throws on malformed JSON or non-array roots.
    /// </summary>
    private static string[]? ParseStringArrayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("expected a JSON array at the root.");
        var list = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.GetRawText());
        }
        return list.ToArray();
    }

    private static string NormalizeForComparison(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string[] SearchTerms(string query) =>
        query.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim('"', '\'', '`', '(', ')', '[', ']', '{', '}', '.', ':'))
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int SearchScore(StoryEntity entity, string query, IReadOnlyList<string> searchTerms)
    {
        var score = TextMatchScore(entity.Name, query, titleWeight: 80, detailWeight: 30);
        score += TextMatchScore(entity.Type, query, titleWeight: 12, detailWeight: 8);
        score += TextMatchScore(entity.Summary, query, titleWeight: 20, detailWeight: 12);
        foreach (var alias in entity.Aliases)
            score += TextMatchScore(alias, query, titleWeight: 30, detailWeight: 16);
        foreach (var section in entity.WikiSections)
        {
            score += TextMatchScore(section.Title, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(section.Body, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var sourceEvidence in entity.SourceEvidence)
        {
            score += TextMatchScore(sourceEvidence.SourceTitle, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(sourceEvidence.Markdown, query, titleWeight: 12, detailWeight: 8);
        }
        foreach (var property in entity.Properties)
        {
            score += TextMatchScore(property.Key, query, titleWeight: 8, detailWeight: 4);
            score += TextMatchScore(property.Value, query, titleWeight: 8, detailWeight: 4);
        }

        foreach (var term in searchTerms)
        {
            score += TextMatchScore(entity.Name, term, titleWeight: 180, detailWeight: 60);
            score += TextMatchScore(entity.Type, term, titleWeight: 16, detailWeight: 8);
            score += TextMatchScore(entity.Summary, term, titleWeight: 28, detailWeight: 14);
            foreach (var alias in entity.Aliases)
                score += TextMatchScore(alias, term, titleWeight: 70, detailWeight: 24);
            foreach (var section in entity.WikiSections)
            {
                score += TextMatchScore(section.Title, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(section.Body, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var sourceEvidence in entity.SourceEvidence)
            {
                score += TextMatchScore(sourceEvidence.SourceTitle, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(sourceEvidence.Markdown, term, titleWeight: 18, detailWeight: 10);
            }
            foreach (var property in entity.Properties)
            {
                score += TextMatchScore(property.Key, term, titleWeight: 10, detailWeight: 5);
                score += TextMatchScore(property.Value, term, titleWeight: 10, detailWeight: 5);
            }
        }

        return score;
    }

    private static int TextMatchScore(string? value, string query, int titleWeight, int detailWeight)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(query)) return 0;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 4;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return titleWeight * 2;
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? detailWeight : 0;
    }

    private static object CompactEntitySearchPayload(StoryEntity entity, int score, IReadOnlyList<EntityVisualExampleView>? visuals = null) => new
    {
        id = entity.Id,
        type = entity.Type,
        name = entity.Name,
        order = entity.Order,
        parentId = entity.ParentId,
        origin = entity.IsIngestCreated ? "source-derived" : "project-owned",
        entity.IsIngestCreated,
        matchScore = score,
        previewIsComplete = false,
        previewCounts = new
        {
            summaryCharacters = entity.Summary?.Length ?? 0,
            aliases = entity.Aliases.Count,
            wikiSections = entity.WikiSections.Count,
            sourceEvidence = entity.SourceEvidence.Count,
            properties = entity.Properties.Count,
            visuals = visuals?.Count ?? 0,
        },
        preview = new
        {
            summaryText = TruncatePropertyValue(entity.Summary),
            aliases = entity.Aliases.Take(4).ToArray(),
            properties = CompactProperties(entity.Properties),
            canonicalVisualReferences = (visuals ?? []).Take(4).Select(example => new
            {
                example.Image.Id,
                example.Label,
                example.SortOrder,
                altText = TruncatePropertyValue(example.Image.AltText),
            }),
        },
        detailReadTool = "read_entity",
        detailReadArguments = new { entityId = entity.Id, pageNumber = 1 },
    };

    private static IReadOnlyList<Guid>? ParseSourceIds(string[]? sourceIds, out string? error)
    {
        error = null;
        if (sourceIds is not { Length: > 0 }) return null;

        var parsed = new List<Guid>();
        foreach (var value in sourceIds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!Guid.TryParse(value, out var id))
            {
                error = $"Error: sourceId '{value}' is not a valid Guid.";
                return null;
            }
            parsed.Add(id);
        }

        return parsed.Count == 0 ? null : parsed;
    }

    private static object ChapterPayload(Chapter chapter) => new
    {
        id = chapter.Id,
        actId = chapter.ActId,
        order = chapter.Order,
        title = chapter.Title,
        synopsis = chapter.Synopsis,
    };

    private static bool HasMeaningfulManuscriptContent(ManuscriptDocument manuscript) =>
        manuscript.Content.Any(block =>
            block.Type is ManuscriptBlockType.SceneBreak
                or ManuscriptBlockType.Figure
                or ManuscriptBlockType.DesignedPage
            || !string.IsNullOrWhiteSpace(ManuscriptCodec.Text(block)));

    private static object BookBriefPayload(BookBrief brief) => new
    {
        brief.Id,
        brief.ProjectId,
        brief.BookKind,
        brief.Premise,
        brief.Genre,
        brief.PrimaryThemes,
        brief.Purpose,
        brief.CreativeConstraints,
        brief.TargetAudience,
        brief.MinimumReaderAge,
        brief.MaximumReaderAge,
        brief.ReadingLevelGuidance,
        brief.TargetWordCount,
        brief.PointOfView,
        brief.Tense,
        brief.VoiceAndTone,
        brief.LanguageLocale,
        brief.HouseStyle,
        brief.ReadAloudPriority,
        brief.AccessibilityGoals,
        brief.VisualDirection,
        brief.UpdatedAt,
    };

    private static Dictionary<string, string?> CompactProperties(IReadOnlyDictionary<string, string?> properties)
    {
        var compact = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties
            .Where(property => !string.Equals(property.Key, "order", StringComparison.OrdinalIgnoreCase))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Take(4))
        {
            compact[property.Key] = TruncatePropertyValue(property.Value);
        }

        return compact;
    }

    private static string? TruncatePropertyValue(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= 240 ? value : value[..240] + "...";

    private static bool IsSearchableEntityType(string type) =>
        !string.Equals(type, EntityTypeService.ProjectNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ActNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ChapterNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.ProjectFactNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceChunkNodeType, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type, EntityTypeService.SourceBlockNodeType, StringComparison.OrdinalIgnoreCase);

    private static string? FormatLineRange(
        string body,
        int? startLine,
        int? endLine,
        out string numbered,
        out string? rangeLabel)
    {
        numbered = string.Empty;
        rangeLabel = null;

        if (startLine is null && endLine is null)
        {
            numbered = ChapterFormatting.WithLineNumbers(body);
            return null;
        }

        var lines = ChapterFormatting.SplitLines(body);
        if (lines.Count == 0)
        {
            rangeLabel = "empty chapter";
            return null;
        }

        var start = startLine ?? 1;
        var end = endLine ?? lines.Count;
        if (start < 1) return "Error: startLine must be 1 or greater.";
        if (end < 1) return "Error: endLine must be 1 or greater.";
        if (start > end) return "Error: startLine must be less than or equal to endLine.";
        if (start > lines.Count) return $"Error: startLine {start} is beyond the chapter's {lines.Count} lines.";

        end = Math.Min(end, lines.Count);
        var width = Math.Max(4, lines.Count.ToString().Length);
        var sb = new StringBuilder();
        for (var i = start - 1; i < end; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(width, '0'));
            sb.Append(": ");
            sb.Append(lines[i]);
            if (i < end - 1) sb.Append('\n');
        }

        numbered = sb.ToString();
        rangeLabel = $"lines {start}-{end} of {lines.Count}";
        return null;
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// Resolves the loosely-typed <c>actId</c> string the model passes (a Guid, "unassigned",
    /// or null/empty) into a <see cref="Guid?"/>. Validates that any supplied act belongs to
    /// the current project. Returns <c>(null, errorMessage)</c> on failure.
    /// </summary>
    private async Task<(Guid? Resolved, string? Error)> ResolveActAsync(
        OutlineCollaborationContext ctx,
        string? actId,
        bool allowUnassigned)
    {
        if (string.IsNullOrWhiteSpace(actId) || string.Equals(actId, UnassignedSentinel, StringComparison.OrdinalIgnoreCase))
        {
            if (!allowUnassigned) return (null, "Error: actId is required.");
            return (null, null);
        }

        if (!Guid.TryParse(actId, out var parsed))
            return (null, $"Error: actId '{actId}' is not a valid Guid or 'unassigned'.");

        var act = await acts.GetAsync(parsed);
        if (act is null || act.ProjectId != ctx.ProjectId)
            return (null, $"Error: act {parsed} not found in this project.");

        return (parsed, null);
    }

}

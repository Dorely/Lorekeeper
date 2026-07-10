using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.EditorChat;

public sealed class EditorChatTools(
    IActService acts,
    IChapterService chapters,
    IEntityService entities,
    IEntityTypeService entityTypes,
    IProjectFactService projectFacts,
    IEditorContextService editorContext,
    IEntityRelationContextService entityRelations,
    IProjectSearchService projectSearch,
    IEditorRevisionAgentService revisionAgents,
    OutlineCollaborationTools outlineTools,
    IProjectImageService projectImages,
    IEntityVisualExampleService entityVisualExamples,
    IProjectImageJobService imageJobs,
    IProjectImageGenerationRuntime imageRuntime,
    IChapterVisualService chapterVisuals,
    IOptions<EditorChatOptions> editorOptions,
    IOptions<ProjectImageGenerationOptions> imageOptions)
{
    private static readonly EntityRelationContextOptions _listEntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 8,
        MaxTraversalPaths = 10,
        MaxLinksPerNode = 8,
    };

    private static readonly EntityRelationContextOptions _detailEntityRelationOptions = new()
    {
        Depth = 2,
        MaxDirectLinks = 16,
        MaxTraversalPaths = 24,
        MaxLinksPerNode = 10,
    };

    private const int EditChapterExcerptContextLines = 3;

    public async Task<IList<AITool>> BuildAsync(
        EditorChatContext context,
        EditorChatToolMode mode = EditorChatToolMode.Normal,
        CancellationToken cancellationToken = default)
    {
        var tools = new List<AITool>();
        var impactDescription = "Read-only book-level impact map for continuity changes. Combines outline order, chapter synopses, server-side keyword/body checks, hybrid project search hits, affected entities/events, adjacency, and downstream chapters from an anchor chapter. Use this before spawning revision agents or before deciding which chapters need body edits.";

        tools.AddRange([
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Resolve searchable project source ids by title/name/type. Use this before a source-filtered search when the user names a source but you need its id."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber),
                name: "read_project_source",
                description: "Read one paginated project source by sourceType and sourceId. Supports chapters, acts, entities, ingest sources, raw ingest source text, and ingest source chunks. Use this before searching only inside a specific source text."),

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
                description: "Hybrid keyword + semantic search over indexed project text. Use sourceTypes/sourceIds/containerSourceId to manually restrict search to a specific source, chunk, chapter, act, or entity. Set lexicalOnly=true for exact names/phrases or when the user asks to look in a specific source text."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List every chapter in the current project (id, order, title, synopsis, visual mode, page layout, body line count, and read_chapter page count)."),

            AIFunctionFactory.Create(
                method: (
                    string query,
                    Guid? anchorChapterId = null,
                    Guid[]? affectedEntityIds = null,
                    Guid[]? eventIds = null,
                    string[]? keywords = null,
                    int topK = 20) =>
                    FindImpactedChaptersAsync(context, query, anchorChapterId, affectedEntityIds, eventIds, keywords, topK),
                name: "find_impacted_chapters",
                description: impactDescription),

            AIFunctionFactory.Create(
                method: () => ListProjectFactsAsync(context),
                name: "list_project_facts",
                description: "Read project-level fact ids, keys/values, linked entity ids, and relation context as JSON. The fact text is already in the Context Feed; use this for structured ids, linked-entity grounding, or fact-change verification."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 10, string? type = null, string? parentId = null) =>
                    SearchEntitiesAsync(context, query, topK, type, parentId),
                name: "search_entities",
                description: "Search story graph entities by name, type, property text, aliases, and wiki text. Use optional type or parentId to narrow results. When Review edits is enabled, returns the latest staged entity state from this turn."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ReadEntityAsync(context, entityId),
                name: "read_entity",
                description: "Read one graph entity by id, including properties, structured wiki data, adjacent links, relation context, and visible thumbnail chips for attached visual examples. When Review edits is enabled, returns the latest staged entity and link state from this turn. In normal editor chat, this also adds the entity to the active chapter's Context Feed."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityLinksAsync(context, entityId),
                name: "list_entity_links",
                description: "List all graph links adjacent to an entity, including structural HasChild links and semantic story relationships. When Review edits is enabled, includes staged entity and link changes from this turn."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityVisualExamplesAsync(context, entityId),
                name: "list_entity_visual_examples",
                description: "List the ordered visual examples attached to one entity and show them as visible thumbnail chips. Full image bytes are supplied on the next iteration when the provider is vision-ready."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? pageNumber = null) =>
                    ReadChapterAsync(context, chapterId, pageNumber),
                name: "read_chapter",
                description:
                    "Read one paginated page of a chapter's current body with line numbers (0001: ...). " +
                    "Use chapter ids from the Context Feed outline when available; use list_chapters for missing ids, line counts, and page counts. " +
                    "Provide pageNumber to read a specific page of the full chapter; omit it to read page 1. " +
                    "Always returns content plus pagination metadata. If this turn already staged an edit to the chapter, returns the latest staged body for this turn."),

            AIFunctionFactory.Create(
                method: () => ListProjectImagesAsync(context),
                name: "list_project_images",
                description: "List the project image library with ids, filenames, alt text, source, prompt, model, size, and preview URL. Read-only and available in Contest preparation."),

            AIFunctionFactory.Create(
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image's metadata and expose it as explicit visual context. If the active provider is vision-ready, the image bytes are supplied to the model on the next iteration."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => ReadChapterVisualLayoutAsync(context, chapterId),
                name: "read_chapter_visual_layout",
                description: "Read a chapter's visual mode and layout manifest, render the current composed pages as visible thumbnails, and report PicturePage text-fit diagnostics. Vision-ready providers receive the rendered pages on the next iteration. Use this after PicturePage visual mutations for final visual verification. Read-only and available in Contest preparation."),
        ]);

        if (mode == EditorChatToolMode.ContestPreparation)
        {
            tools.Add(AIFunctionFactory.Create(
                method: (Guid chapterId) => StartContestAsync(context, chapterId),
                name: "start_contest",
                description:
                    "Start a Contest Mode generation job for chapter-body mutations. " +
                    "Requires a Prose or IllustratedProse chapter; PicturePage chapters must be edited with visual layout tools. " +
                    "Call this exactly once after gathering enough read-only context. "));
            return tools;
        }

        tools.AddRange([
            AIFunctionFactory.Create(
                method: (Guid entityId, Guid imageId, string? label = null) => AttachProjectImageToEntityAsync(context, entityId, imageId, label),
                name: "attach_project_image_to_entity",
                description: "Attach an existing project image to a non-structural entity as an ordered visual example. Use a concise entity-specific role label such as 'default appearance', 'winter outfit', or 'exterior view'."),
            AIFunctionFactory.Create(
                method: (Guid exampleId, string label, int? sortOrder = null) => UpdateEntityVisualExampleAsync(context, exampleId, label, sortOrder),
                name: "update_entity_visual_example",
                description: "Update an entity visual example's role label and optionally its zero-based order."),
            AIFunctionFactory.Create(
                method: (Guid exampleId) => DetachProjectImageFromEntityAsync(context, exampleId),
                name: "detach_project_image_from_entity",
                description: "Detach one visual example association without deleting the project image."),
            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ProjectImageCropRegion crop, string? fileName = null, string? altText = null, EntityVisualTarget[]? entityTargets = null) =>
                    CropProjectImageAsync(context, sourceImageId, crop, fileName, altText, entityTargets),
                name: "crop_project_image",
                description: "Create a non-destructive project-library crop from an existing image using 0-100 percentage coordinates. Inspect the source first or use user-supplied coordinates, describe only the cropped subject in altText, and pass only explicit entityTargets. Source associations are never inherited."),
        ]);

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, string content, int? startLine = null, int? endLine = null) =>
                EditChapterAsync(context, chapterId, content, startLine, endLine),
            name: "edit_chapter",
            description:
                "Edit a chapter using line-based semantics. " +
                "Requires a Prose or IllustratedProse chapter; PicturePage text must be edited with picture-page layout tools. " +
                "To Append: Leave both startLine and endLine null: appends `content` to the end of the chapter. " +
                "For an empty chapter, leave both startLine and endLine null to write the first content. " +
                "To Insert: Provide only startLine and leave endLine null: insert `content` BEFORE that line (1-based). " +
                "To Replace: Provide both startLine and endLine: replace the inclusive range of existing numbered lines with `content`. " +
                "Lines are 1-based and match the numbering shown by read_chapter and the editor gutter. " +
                "`content` should not contain line numbers. " +
                "Returns a short change summary plus the edited line-numbered excerpt with nearby context lines."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, string visualMode, string? pageLayoutKind = null) =>
                SetChapterVisualModeAsync(context, chapterId, visualMode, pageLayoutKind),
            name: "set_chapter_visual_mode",
            description:
                "Live visual-layout mutation. Set a chapter visual mode to Prose, IllustratedProse, or PicturePage. " +
                "Use pageLayoutKind for IllustratedProse or PicturePage; valid values are SinglePortrait, SingleLandscape, DoublePortrait, and DoubleLandscape."));

        tools.Add(AIFunctionFactory.Create(
            method: (string prompt, string? altText = null, string? size = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, Guid[]? referenceImageIds = null, EntityVisualTarget[]? entityTargets = null, bool placeInCurrentChapter = false, Guid? targetChapterId = null, Guid? targetPictureImageElementId = null) =>
                GenerateProjectImageAsync(context, prompt, altText, size, quality, outputFormat, outputCompression, referenceImageIds, entityTargets, placeInCurrentChapter, targetChapterId, targetPictureImageElementId),
            name: "generate_project_image",
            description:
                "Generate an image and save it to the project image library. Pass entityTargets [{entityId,label}] for every clearly represented entity; successful outputs are attached automatically. Optional referenceImageIds accepts multiple existing project image ids, up to the configured reference-image limit; use attached examples for recurring characters, outfits, settings, props, and style continuity. Do not attach decorative/layout-only art or guess ambiguous associations. " +
                "For PicturePage targets, pass targetChapterId and optionally targetPictureImageElementId; omit size to use the layout-native recommended size. " +
                "Set placeInCurrentChapter=true only when the user wants the generated image inserted into the current chapter immediately; the current chapter must already be IllustratedProse or PicturePage."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, Guid imageId) => AddProjectImageToChapterAsync(context, chapterId, imageId),
            name: "add_project_image_to_chapter",
            description:
                "Live visual-layout mutation. Place an existing project image into an IllustratedProse or PicturePage chapter. Prose chapters must first be converted with set_chapter_visual_mode."));

        tools.Add(AIFunctionFactory.Create(
            method: (
                Guid chapterId,
                Guid imageBlockId,
                string? anchorPosition = null,
                int? paragraphIndex = null,
                double? widthPercent = null,
                string? alignment = null,
                string? caption = null,
                string? altTextOverride = null,
                int? sortOrder = null,
                bool? startOnNewPage = null) =>
                UpdateIllustratedProseImageAsync(context, chapterId, imageBlockId, anchorPosition, paragraphIndex, widthPercent, alignment, caption, altTextOverride, sortOrder, startOnNewPage),
            name: "update_illustrated_prose_image",
            description:
                "Live visual-layout mutation for IllustratedProse chapters only. Update an existing anchored image block's anchorPosition (BeforeParagraph/AfterParagraph), paragraphIndex, widthPercent, alignment (Left/Center/Right), caption, altTextOverride, sortOrder, or startOnNewPage."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, Guid imageBlockId) =>
                RemoveIllustratedProseImageAsync(context, chapterId, imageBlockId),
            name: "remove_illustrated_prose_image",
            description: "Live visual-layout mutation for IllustratedProse chapters only. Remove an anchored image block."));

        tools.Add(AIFunctionFactory.Create(
            method: (
                Guid chapterId,
                Guid imageElementId,
                double? xPercent = null,
                double? yPercent = null,
                double? widthPercent = null,
                double? heightPercent = null,
                string? fit = null,
                double? opacity = null,
                int? zIndex = null,
                string? altTextOverride = null) =>
                UpdatePicturePageImageAsync(context, chapterId, imageElementId, xPercent, yPercent, widthPercent, heightPercent, fit, opacity, zIndex, altTextOverride),
            name: "update_picture_page_image",
            description:
                "Live visual-layout mutation for PicturePage chapters only. Update an existing image element's percentage bounds, fit (Contain/Cover/Fill), opacity, zIndex, or altTextOverride."));

        tools.Add(AIFunctionFactory.Create(
            method: (
                Guid chapterId,
                Guid? textElementId = null,
                string? text = null,
                double? xPercent = null,
                double? yPercent = null,
                double? widthPercent = null,
                double? heightPercent = null,
                int? zIndex = null,
                int? readingOrder = null,
                string? fontFamily = null,
                double? fontSizePercent = null,
                double? lineHeight = null,
                string? color = null,
                string? backgroundColor = null,
                double? backgroundOpacity = null,
                string? textAlign = null,
                string? verticalAlign = null,
                string? shadow = null) =>
                UpsertPicturePageTextAsync(context, chapterId, textElementId, text, xPercent, yPercent, widthPercent, heightPercent, zIndex, readingOrder, fontFamily, fontSizePercent, lineHeight, color, backgroundColor, backgroundOpacity, textAlign, verticalAlign, shadow),
            name: "upsert_picture_page_text",
            description:
                "Live visual-layout mutation for PicturePage chapters only. Create a new text box when textElementId is omitted, or update an existing text box. This is the correct way to edit PicturePage chapter text; it also updates the projected chapter body."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, string elementKind, Guid elementId) =>
                RemovePicturePageElementAsync(context, chapterId, elementKind, elementId),
            name: "remove_picture_page_element",
            description: "Live visual-layout mutation for PicturePage chapters only. Remove an image or text element. elementKind must be image or text."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, Guid imageId) => AddProjectImageToContextAsync(context, chapterId, imageId),
            name: "add_project_image_to_context",
            description: "Add an existing project image as explicit context for a chapter without placing it into the chapter layout."));

        tools.Add(AIFunctionFactory.Create(
            method: (Guid chapterId, Guid imageId) => RemoveProjectImageFromContextAsync(context, chapterId, imageId),
            name: "remove_project_image_from_context",
            description: "Remove an explicit project image context inclusion from a chapter without deleting the image or changing chapter layout."));

        tools.Add(AIFunctionFactory.Create(
            method: (EditorRevisionAgentAssignmentInput[] chapters) => StartRevisionAgentsAsync(context, chapters),
            name: "start_revision_agents",
            description:
                "Run prose-only revision workers that edit their assigned chapter bodies for explicit chapter assignments. " +
                "Each item must include chapterId, reason, and chapter-specific instructions. " +
                "Workers can only alter chapter body text; this coordinator reviews their completed/staged changes and takes follow-up action only if needed. " +
                "Before calling this, make any broader canon, outline, entity, beat, relationship, fact, or synopsis updates yourself."));

        var existingNames = tools.OfType<AIFunction>().Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var outlineTool in await outlineTools.BuildAsync(new OutlineCollaborationContext(context.ProjectId, context.OnMutated, context.OutlineStaging), cancellationToken))
        {
            if (outlineTool is AIFunction function && existingNames.Add(function.Name))
                tools.Add(outlineTool);
        }

        return tools;
    }

    private async Task<string> ListSearchSourcesAsync(
        EditorChatContext ctx,
        string? query,
        string[]? sourceTypes,
        int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(ctx.ProjectId, query, sourceTypes, topK);
        return JsonSerializer.Serialize(sources);
    }

    private async Task<string> ReadProjectSourceAsync(
        EditorChatContext ctx,
        string sourceType,
        Guid sourceId,
        int? pageNumber)
    {
        var result = await projectSearch.ReadSourceAsync(ctx.ProjectId, sourceType, sourceId, pageNumber);
        if (result is null) return $"Error: source {sourceType}/{sourceId:N} was not found in this project.";
        if (string.Equals(sourceType, ProjectSearchSourceTypes.Entity, StringComparison.OrdinalIgnoreCase))
            await AddEntityVisualsToModelAsync(ctx, sourceId);
        return JsonSerializer.Serialize(result);
    }

    private async Task<string> SearchProjectAsync(
        EditorChatContext ctx,
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
            lexicalOnly));

        return results.Count == 0
            ? "No matches."
            : JsonSerializer.Serialize(results.Select(SearchResultPayload));
    }

    private async Task<string> ListChaptersAsync(EditorChatContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var sb = new StringBuilder();
        foreach (var chapter in list)
        {
            var lineCount = ChapterFormatting.SplitLines(chapter.Body).Count;
            var pageCount = CountReadChapterPages(chapter.Body, EffectiveReadChapterPageMaxChars());

            sb.Append(chapter.Order + 1).Append(". ").Append(chapter.Title)
              .Append(" - id=").Append(chapter.Id)
              .Append(" - visualMode=").Append(chapter.VisualMode)
              .Append(" - pageLayoutKind=").Append(chapter.PageLayoutKind)
              .Append(" - lines=").Append(lineCount)
              .Append(" - bodyChars=").Append(chapter.Body.Length)
              .Append(" - readChapterPages=").Append(pageCount);
            if (!string.IsNullOrWhiteSpace(chapter.Synopsis))
                sb.Append(" - ").Append(chapter.Synopsis);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> FindImpactedChaptersAsync(
        EditorChatContext ctx,
        string query,
        Guid? anchorChapterId,
        Guid[]? affectedEntityIds,
        Guid[]? eventIds,
        string[]? keywords,
        int topK)
    {
        var terms = BuildImpactTerms(query, keywords);
        if (terms.Count == 0)
            return "Error: query or keywords are required.";

        topK = Math.Clamp(topK, 1, 50);
        var orderedChapters = await ListOrderedChaptersAsync(ctx.ProjectId);
        if (orderedChapters.Count == 0)
            return "No chapters in this project.";

        var candidates = orderedChapters.ToDictionary(
            item => item.Chapter.Id,
            item => new ImpactCandidate(item.Chapter, item.GlobalOrder));

        if (anchorChapterId is { } anchor && candidates.TryGetValue(anchor, out var anchorCandidate))
        {
            anchorCandidate.Add(45, "anchor", "Anchor chapter supplied by the coordinator.");
            foreach (var candidate in candidates.Values.Where(candidate => candidate.GlobalOrder > anchorCandidate.GlobalOrder))
            {
                var distance = candidate.GlobalOrder - anchorCandidate.GlobalOrder;
                var score = Math.Max(10, 35 - Math.Min(distance, 10) * 2);
                candidate.Add(score, "downstream", $"Chapter is {distance} chapter(s) after the anchor chapter.");
            }

            foreach (var candidate in candidates.Values.Where(candidate => Math.Abs(candidate.GlobalOrder - anchorCandidate.GlobalOrder) == 1))
                candidate.Add(20, "adjacent", "Chapter is adjacent to the anchor chapter.");
        }

        foreach (var candidate in candidates.Values)
        {
            ScoreText(candidate, "title", candidate.Chapter.Title, terms, 24);
            ScoreText(candidate, "synopsis", candidate.Chapter.Synopsis, terms, 30);
            ScoreText(candidate, "body-keyword", candidate.Chapter.Body, terms, 18);
        }

        await ScoreProjectSearchHitsAsync(ctx.ProjectId, terms, candidates);
        await ScoreEntityAnchorsAsync(ctx.ProjectId, affectedEntityIds, "affected-entity", candidates);
        await ScoreEntityAnchorsAsync(ctx.ProjectId, eventIds, "affected-event", candidates);

        var results = candidates.Values
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.GlobalOrder)
            .Take(topK)
            .Select(candidate => new
            {
                chapterId = candidate.Chapter.Id,
                title = candidate.Chapter.Title,
                order = candidate.GlobalOrder,
                synopsis = candidate.Chapter.Synopsis,
                bodyStats = new
                {
                    lines = ChapterFormatting.SplitLines(candidate.Chapter.Body).Count,
                    chars = candidate.Chapter.Body.Length,
                    readChapterPages = CountReadChapterPages(candidate.Chapter.Body, EffectiveReadChapterPageMaxChars()),
                },
                score = candidate.Score,
                impact = candidate.Score >= 90 ? "high" : candidate.Score >= 45 ? "medium" : "low",
                reasons = candidate.Reasons,
                evidence = candidate.Evidence.Take(6).ToList(),
            })
            .ToList();

        if (results.Count == 0)
            return JsonSerializer.Serialize(new
            {
                query,
                message = "No impacted chapters found from the available outline, entity, keyword, and project-search signals.",
                candidates = Array.Empty<object>(),
            });

        return JsonSerializer.Serialize(new
        {
            query,
            anchors = new
            {
                anchorChapterId,
                affectedEntityIds = affectedEntityIds ?? [],
                eventIds = eventIds ?? [],
                keywords = keywords ?? [],
            },
            candidates = results,
        });
    }

    private async Task<string> StartRevisionAgentsAsync(EditorChatContext ctx, EditorRevisionAgentAssignmentInput[] assignments)
    {
        if (assignments is null || assignments.Length == 0)
            return "Error: chapters is required.";

        foreach (var assignment in assignments)
        {
            if (assignment is null)
                return "Error: each revision-agent assignment is required.";
            var chapter = await chapters.GetAsync(assignment.ChapterId);
            if (chapter is null || chapter.ProjectId != ctx.ProjectId)
                return $"Error: chapter {assignment.ChapterId} not found in this project.";
            if (chapter.VisualMode == ChapterVisualMode.PicturePage)
                return $"Error: start_revision_agents cannot target PicturePage chapter '{chapter.Title}' ({chapter.Id}) because revision workers can only edit prose. Use Picture Page visual layout tools instead.";
        }

        var request = new EditorRevisionAgentRunRequest(
            ctx.ProjectId,
            ctx.ConversationId,
            ctx.CurrentAssistantMessageId,
            ctx.CurrentToolCallId,
            ctx.CurrentArgumentsJson,
            assignments);
        var result = await revisionAgents.RunAsync(request);
        if (!ctx.ReviewEdits && result.Sessions.Any(session => session.Status == EditorRevisionSessionStatus.Completed))
            ctx.OnMutated();
        return EditorRevisionAgentService.SerializeRunResult(result);
    }

    private async Task<string> ListProjectFactsAsync(EditorChatContext ctx)
    {
        var facts = await projectFacts.ListAsync(ctx.ProjectId);
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
                    relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, link.EntityId, _listEntityRelationOptions),
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

    private async Task<string> SearchEntitiesAsync(
        EditorChatContext ctx,
        string query,
        int topK,
        string? type,
        string? parentId)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query is required.";
        topK = Math.Clamp(topK, 1, 20);

        Guid? parent = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!Guid.TryParse(parentId, out var parsedParent))
                return $"Error: parentId '{parentId}' is not a valid Guid.";
            parent = parsedParent;
        }

        if (ctx.ReviewEdits && ctx.OutlineStaging is not null)
            return await ctx.OutlineStaging.SearchEntitiesAsync(query, topK, type, parent);

        var searchTerms = SearchTerms(query);

        var matches = new List<(StoryEntity Entity, int Score)>();
        var typeNames = await SearchableTypeNamesAsync(ctx.ProjectId, type);
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

        return JsonSerializer.Serialize(payload);
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

    private async Task<string> ReadEntityAsync(EditorChatContext ctx, Guid entityId)
    {
        if (ctx.ReviewEdits && ctx.OutlineStaging is not null)
        {
            var stagedEntityType = await ctx.OutlineStaging.GetEntityTypeAsync(entityId);
            if (stagedEntityType is null)
                return $"Error: entity {entityId} not found in this project.";

            var stagedAddedToContextFeed = false;
            if (ctx.AutoPinReadEntities && ctx.CurrentChapterId is { } stagedCurrentChapterId && IsSearchableEntityType(stagedEntityType))
            {
                await editorContext.SetItemIncludedAsync(
                    ctx.ProjectId,
                    stagedCurrentChapterId,
                    ContextItemKind.Entity,
                    EditorContextKeys.Entity(entityId),
                    isIncluded: true);
                ctx.OnMutated();
                stagedAddedToContextFeed = true;
            }

            await AddEntityVisualsToModelAsync(ctx, entityId);
            return await ctx.OutlineStaging.ReadEntityAsync(entityId, stagedAddedToContextFeed, _detailEntityRelationOptions);
        }

        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null)
            return $"Error: entity {entityId} not found in this project.";

        var addedToContextFeed = false;
        if (ctx.AutoPinReadEntities && ctx.CurrentChapterId is { } currentChapterId && IsSearchableEntityType(entity.Type))
        {
            await editorContext.SetItemIncludedAsync(
                ctx.ProjectId,
                currentChapterId,
                ContextItemKind.Entity,
                EditorContextKeys.Entity(entity.Id),
                isIncluded: true);
            ctx.OnMutated();
            addedToContextFeed = true;
        }

        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        var manualLinks = links.Where(link => !link.IsAutoLink).Select(LinkPayload).ToList();
        var autoMentionLinks = links.Where(link => link.IsAutoLink).Select(LinkPayload).ToList();
        var relationContext = await entityRelations.BuildForEntityAsync(ctx.ProjectId, entityId, _detailEntityRelationOptions);
        var visualExamples = await AddEntityVisualsToModelAsync(ctx, entityId);
        return JsonSerializer.Serialize(new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            addedToContextFeed,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            canonSources = entity.CanonSources,
            visualExamples = visualExamples.Select(VisualExamplePayload),
            links = manualLinks.Concat(autoMentionLinks),
            manualLinks,
            autoMentionLinks,
            relationContext,
        });
    }

    private async Task<string> ListEntityVisualExamplesAsync(EditorChatContext ctx, Guid entityId)
    {
        if (await entities.GetAsync(ctx.ProjectId, entityId) is null)
            return $"Error: entity {entityId} not found in this project.";
        var examples = await AddEntityVisualsToModelAsync(ctx, entityId);
        return JsonSerializer.Serialize(examples.Select(VisualExamplePayload));
    }

    private async Task<string> AttachProjectImageToEntityAsync(EditorChatContext ctx, Guid entityId, Guid imageId, string? label)
    {
        try
        {
            if (ctx.OutlineStaging is not null)
            {
                var after = new EntityVisualChange("attach", EntityId: entityId, ImageId: imageId, Label: label?.Trim() ?? string.Empty);
                return await ctx.OutlineStaging.StageExternalChangeAsync(
                    "Attach a visual example to an entity", null, after,
                    new { status = "staged", entityId, imageId, label }, "EntityVisualExample", $"{entityId:N}/{imageId:N}");
            }
            var example = await entityVisualExamples.AttachAsync(ctx.ProjectId, entityId, imageId, label, EntityVisualExampleOrigin.Agent);
            ctx.AddModelOnlyImage(example.Image);
            ctx.OnMutated();
            return JsonSerializer.Serialize(VisualExamplePayload(example));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> UpdateEntityVisualExampleAsync(EditorChatContext ctx, Guid exampleId, string label, int? sortOrder)
    {
        try
        {
            if (ctx.OutlineStaging is not null)
            {
                var current = await entityVisualExamples.GetAsync(ctx.ProjectId, exampleId);
                if (current is null) return "Error: entity visual example was not found.";
                var before = new EntityVisualChange("update", current.Id, current.EntityId, current.Image.Id, Label: current.Label, SortOrder: current.SortOrder);
                var after = before with { Label = label.Trim(), SortOrder = sortOrder ?? current.SortOrder };
                return await ctx.OutlineStaging.StageExternalChangeAsync(
                    "Update an entity visual example", before, after,
                    new { status = "staged", exampleId, label, sortOrder }, "EntityVisualExample", exampleId.ToString("N"));
            }
            var example = await entityVisualExamples.UpdateAsync(ctx.ProjectId, exampleId, label, sortOrder);
            ctx.OnMutated();
            return JsonSerializer.Serialize(VisualExamplePayload(example));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> DetachProjectImageFromEntityAsync(EditorChatContext ctx, Guid exampleId)
    {
        if (ctx.OutlineStaging is not null)
        {
            var current = await entityVisualExamples.GetAsync(ctx.ProjectId, exampleId);
            if (current is null) return "Error: entity visual example was not found.";
            var before = new EntityVisualChange("detach", current.Id, current.EntityId, current.Image.Id, Label: current.Label, SortOrder: current.SortOrder);
            return await ctx.OutlineStaging.StageExternalChangeAsync(
                "Detach an entity visual example", before, null,
                new { status = "staged", exampleId }, "EntityVisualExample", exampleId.ToString("N"));
        }
        await entityVisualExamples.DetachAsync(ctx.ProjectId, exampleId);
        ctx.OnMutated();
        return JsonSerializer.Serialize(new { status = "detached", exampleId });
    }

    private async Task<string> CropProjectImageAsync(
        EditorChatContext ctx,
        Guid sourceImageId,
        ProjectImageCropRegion crop,
        string? fileName,
        string? altText,
        EntityVisualTarget[]? entityTargets)
    {
        try
        {
            var image = await projectImages.CropAsync(ctx.ProjectId, sourceImageId, new ProjectImageCropRequest(
                crop,
                fileName?.Trim() ?? string.Empty,
                altText?.Trim() ?? string.Empty));
            var targets = (entityTargets ?? [])
                .Where(target => target.EntityId != Guid.Empty)
                .DistinctBy(target => target.EntityId)
                .ToList();
            var associations = new List<object>();
            foreach (var target in targets)
            {
                if (ctx.OutlineStaging is null)
                {
                    var example = await entityVisualExamples.AttachAsync(
                        ctx.ProjectId,
                        target.EntityId,
                        image.Id,
                        target.Label,
                        EntityVisualExampleOrigin.Agent);
                    associations.Add(VisualExamplePayload(example));
                    continue;
                }

                var after = new EntityVisualChange("attach", EntityId: target.EntityId, ImageId: image.Id, Label: target.Label?.Trim() ?? string.Empty);
                var staged = await ctx.OutlineStaging.StageExternalChangeAsync(
                    $"Attach cropped image to entity {target.EntityId:N}",
                    null,
                    after,
                    new { status = "staged", target.EntityId, imageId = image.Id, target.Label },
                    "EntityVisualExample",
                    $"{target.EntityId:N}/{image.Id:N}");
                associations.Add(JsonSerializer.Deserialize<JsonElement>(staged));
            }

            ctx.AddVisual(await BuildVisualAsync(ctx, image, image.FileName, "Cropped project image saved to the library."));
            ctx.AddModelOnlyImage(image);
            ctx.OnMutated();
            return JsonSerializer.Serialize(new
            {
                status = "cropped",
                sourceImageId,
                image = new
                {
                    image.Id,
                    image.FileName,
                    image.ContentType,
                    image.PreviewUrl,
                    image.AltText,
                    image.Source,
                    image.SizeBytes,
                },
                associations,
            });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> AddEntityVisualsToModelAsync(EditorChatContext ctx, Guid entityId)
    {
        var examples = await entityVisualExamples.ListForEntityAsync(ctx.ProjectId, entityId);
        foreach (var example in examples)
        {
            ctx.AddModelOnlyImage(example.Image);
            ctx.AddVisual(await BuildVisualAsync(
                ctx,
                example.Image,
                title: example.Image.FileName,
                caption: string.IsNullOrWhiteSpace(example.Label)
                    ? $"Visual example for {example.EntityName}."
                    : $"{example.EntityName}: {example.Label}."));
        }
        return examples;
    }

    private static object VisualExamplePayload(EntityVisualExampleView example) => new
    {
        example.Id,
        example.EntityId,
        example.EntityType,
        example.EntityName,
        example.Label,
        example.SortOrder,
        example.Origin,
        image = new
        {
            example.Image.Id,
            example.Image.FileName,
            example.Image.ContentType,
            example.Image.PreviewUrl,
            example.Image.AltText,
            example.Image.Prompt,
        },
    };

    private async Task<object> EntityPayloadAsync(
        Guid projectId,
        StoryEntity entity,
        EntityRelationContextOptions relationOptions)
    {
        var relationContext = await entityRelations.BuildForEntityAsync(projectId, entity.Id, relationOptions);
        return new
        {
            id = entity.Id,
            type = entity.Type,
            name = entity.Name,
            order = entity.Order,
            parentId = entity.ParentId,
            properties = entity.Properties,
            summary = entity.Summary,
            aliases = entity.Aliases,
            wikiSections = entity.WikiSections,
            canonSources = entity.CanonSources,
            relationContext,
        };
    }

    private async Task<string> ListEntityLinksAsync(EditorChatContext ctx, Guid entityId)
    {
        if (ctx.ReviewEdits && ctx.OutlineStaging is not null)
            return await ctx.OutlineStaging.ListEntityLinksAsync(entityId);

        var links = await entities.ListLinksAsync(ctx.ProjectId, entityId);
        return JsonSerializer.Serialize(links.Select(LinkPayload));
    }

    private async Task<string> ReadChapterAsync(
        EditorChatContext ctx,
        Guid chapterId,
        int? pageNumber)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var body = chapter.Body;
        var source = "persisted";
        if (ctx.ReviewEdits && ctx.EditorStaging?.TryGetChapterBodyDraft(chapter.Id, out var draftBody) == true)
        {
            body = draftBody;
            source = "stagedDraft";
        }

        var requestedPageNumber = pageNumber ?? 1;
        if (requestedPageNumber < 1)
            return "Error: pageNumber must be 1 or greater.";

        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var lines = ChapterFormatting.SplitLines(body);

        if (lines.Count == 0)
        {
            if (requestedPageNumber > 1)
                return "Error: pageNumber 1 is the only page available for an empty chapter.";

            return JsonSerializer.Serialize(new
            {
                chapter = new
                {
                    id = chapter.Id,
                    chapter.Title,
                    chapter.Synopsis,
                    source,
                },
                request = new
                {
                    pageNumber = requestedPageNumber,
                },
                chapterStats = new
                {
                    totalChapterLines = 0,
                    totalChapterChars = body.Length,
                },
                pagination = new
                {
                    pageStartLine = 0,
                    pageEndLine = 0,
                    pageStartColumn = 0,
                    pageEndColumn = 0,
                    currentPage = 1,
                    pageCount = 1,
                    pageMaxChars,
                    contentCharCount = 7,
                    hasPreviousPage = false,
                    hasNextPage = false,
                    previousPageNumber = (int?)null,
                    nextPageNumber = (int?)null,
                    startsInsideLine = false,
                    endsInsideLine = false,
                    containsPartialLine = false,
                },
                previousPageArguments = (object?)null,
                nextPageArguments = (object?)null,
                content = "(empty)",
            });
        }

        var pages = BuildReadChapterPages(lines, pageMaxChars);
        if (requestedPageNumber > pages.Count)
            return $"Error: pageNumber {requestedPageNumber} is beyond the chapter's {pages.Count} page(s).";

        var selectedPage = pages[requestedPageNumber - 1];
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var content = FormatReadChapterPageContent(selectedPage, lineNumberWidth);

        return JsonSerializer.Serialize(new
        {
            chapter = new
            {
                id = chapter.Id,
                chapter.Title,
                chapter.Synopsis,
                chapter.VisualMode,
                chapter.PageLayoutKind,
                source,
            },
            request = new
            {
                pageNumber = requestedPageNumber,
            },
            chapterStats = new
            {
                totalChapterLines = lines.Count,
                totalChapterChars = body.Length,
            },
            pagination = new
            {
                pageStartLine = selectedPage.PageStartLine,
                pageEndLine = selectedPage.PageEndLine,
                pageStartColumn = selectedPage.PageStartColumn,
                pageEndColumn = selectedPage.PageEndColumn,
                currentPage = requestedPageNumber,
                pageCount = pages.Count,
                pageMaxChars,
                contentCharCount = content.Length,
                hasPreviousPage = requestedPageNumber > 1,
                hasNextPage = requestedPageNumber < pages.Count,
                previousPageNumber = requestedPageNumber > 1 ? requestedPageNumber - 1 : (int?)null,
                nextPageNumber = requestedPageNumber < pages.Count ? requestedPageNumber + 1 : (int?)null,
                startsInsideLine = selectedPage.StartsInsideLine,
                endsInsideLine = selectedPage.EndsInsideLine,
                containsPartialLine = selectedPage.ContainsPartialLine,
            },
            previousPageArguments = requestedPageNumber > 1
                ? new { chapterId = chapter.Id, pageNumber = requestedPageNumber - 1 }
                : null,
            nextPageArguments = requestedPageNumber < pages.Count
                ? new { chapterId = chapter.Id, pageNumber = requestedPageNumber + 1 }
                : null,
            content,
        });
    }

    private async Task<string> ListProjectImagesAsync(EditorChatContext ctx)
    {
        var images = await projectImages.ListAsync(ctx.ProjectId);
        if (images.Count == 0)
            return "No project images.";

        return JsonSerializer.Serialize(images.Select(image => new
        {
            image.Id,
            image.FileName,
            image.ContentType,
            image.PreviewUrl,
            image.AltText,
            image.Source,
            image.Prompt,
            image.GenerationModel,
            image.CreatedAt,
            image.UpdatedAt,
            image.SizeBytes,
        }));
    }

    private async Task<string> ReadProjectImageAsync(EditorChatContext ctx, Guid imageId)
    {
        var image = await projectImages.GetAsync(ctx.ProjectId, imageId);
        if (image is null)
            return $"Error: image {imageId:N} was not found in this project.";

        ctx.AddVisual(await BuildVisualAsync(
            ctx,
            image,
            title: image.FileName,
            caption: "Image read into Editor Chat context."));
        ctx.AddModelOnlyImage(image);

        return JsonSerializer.Serialize(new
        {
            image = new
            {
                image.Id,
                image.FileName,
                image.ContentType,
                image.PreviewUrl,
                image.AltText,
                image.Source,
                image.Prompt,
                image.GenerationModel,
                image.CreatedAt,
                image.UpdatedAt,
                image.SizeBytes,
            },
            delivery = ctx.VisionReady
                ? "full image bytes will be supplied to the model on the next iteration"
                : "metadata only; the active chat provider is not vision-ready",
        });
    }

    private async Task<string> ReadChapterVisualLayoutAsync(EditorChatContext ctx, Guid chapterId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var state = await chapterVisuals.GetAsync(chapterId);
        if (state is null)
            return $"Error: visual layout for chapter {chapterId} was not found.";

        var imageNames = (await projectImages.ListAsync(ctx.ProjectId))
            .ToDictionary(image => image.Id, image => image.FileName);
        var snapshots = await chapterVisuals.RenderSnapshotsAsync(chapterId);
        foreach (var snapshot in snapshots)
        {
            var visualId = Guid.NewGuid();
            var size = ReadSize(snapshot.Data);
            ctx.AddVisual(new EditorChatVisualAttachment(
                visualId,
                $"Rendered page {snapshot.PageNumber}",
                $"Current rendered layout for chapter '{chapter.Title}'.",
                $"/projects/{ctx.ProjectId:N}/editor-chat-visuals/{visualId:N}/content?maxEdge=640",
                $"/projects/{ctx.ProjectId:N}/editor-chat-visuals/{visualId:N}/content",
                size.Width,
                size.Height,
                ctx.CurrentToolCallId,
                SourceKind: "renderedChapterSnapshot",
                SourceRefId: chapter.Id,
                ContentType: snapshot.ContentType,
                FileName: snapshot.FileName,
                Data: snapshot.Data));
            ctx.AddModelOnlyImage(visualId, snapshot.FileName, snapshot.ContentType, snapshot.Data);
        }

        var textFitDiagnostics = snapshots
            .SelectMany(snapshot => snapshot.TextFitDiagnostics.Select(diagnostic => new
            {
                snapshot.PageNumber,
                diagnostic.ElementId,
                diagnostic.WrappedLineCount,
                diagnostic.DrawnLineCount,
                diagnostic.AvailableHeightPixels,
                diagnostic.RequiredHeightPixels,
                diagnostic.Fits,
            }))
            .ToList();
        return JsonSerializer.Serialize(new
        {
            chapter = new
            {
                id = chapter.Id,
                chapter.Title,
                chapter.Synopsis,
            },
            state.VisualMode,
            state.PageLayoutKind,
            state.IllustrationLayout,
            state.PageLayout,
            projectedBody = state.VisualMode == ChapterVisualMode.PicturePage ? chapter.Body : null,
            manifest = chapterVisuals.BuildManifest(state, imageNames),
            renderedSnapshots = snapshots.Select(snapshot =>
            {
                var size = ReadSize(snapshot.Data);
                return new
                {
                    snapshot.PageNumber,
                    snapshot.FileName,
                    snapshot.ContentType,
                    size.Width,
                    size.Height,
                };
            }),
            textFit = new
            {
                applicable = state.VisualMode == ChapterVisualMode.PicturePage,
                allTextFits = state.VisualMode == ChapterVisualMode.PicturePage && snapshots.Count > 0
                    ? textFitDiagnostics.All(diagnostic => diagnostic.Fits)
                    : (bool?)null,
                elements = textFitDiagnostics,
            },
            delivery = snapshots.Count == 0
                ? "no rendered snapshots were produced for this layout"
                : ctx.VisionReady
                    ? "rendered snapshot thumbnails are visible in chat and full image bytes will be supplied to the model on the next iteration"
                    : "rendered snapshot thumbnails are visible in chat; the active chat provider is not vision-ready, so use the manifest and text-fit diagnostics",
        });
    }

    private async Task<string> SetChapterVisualModeAsync(
        EditorChatContext ctx,
        Guid chapterId,
        string visualMode,
        string? pageLayoutKind)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        if (!Enum.TryParse<ChapterVisualMode>(visualMode, ignoreCase: true, out var parsedMode)
            || !Enum.IsDefined(parsedMode))
        {
            return "Error: visualMode must be Prose, IllustratedProse, or PicturePage.";
        }

        ChapterPageLayoutKind? parsedLayout = null;
        if (!string.IsNullOrWhiteSpace(pageLayoutKind))
        {
            if (!Enum.TryParse<ChapterPageLayoutKind>(pageLayoutKind, ignoreCase: true, out var candidate)
                || !Enum.IsDefined(candidate))
            {
                return "Error: pageLayoutKind must be SinglePortrait, SingleLandscape, DoublePortrait, or DoubleLandscape.";
            }

            parsedLayout = candidate;
        }
        if (parsedMode == ChapterVisualMode.Prose && parsedLayout is not null)
            return "Error: pageLayoutKind only applies to IllustratedProse or PicturePage chapters. Omit pageLayoutKind when setting Prose.";

        var state = await chapterVisuals.SetModeAsync(
            chapterId,
            new ChapterVisualModeUpdate(parsedMode, parsedLayout));
        ctx.OnMutated();
        return await VisualStatePayloadAsync(ctx, state, $"Chapter visual mode set to {state.VisualMode}.");
    }

    private async Task<string> GenerateProjectImageAsync(
        EditorChatContext ctx,
        string prompt,
        string? altText,
        string? size,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        Guid[]? referenceImageIds,
        EntityVisualTarget[]? entityTargets,
        bool placeInCurrentChapter,
        Guid? targetChapterId,
        Guid? targetPictureImageElementId)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: prompt is required.";
        if (placeInCurrentChapter && ctx.CurrentChapterId is null)
            return "Error: placeInCurrentChapter requires an active current chapter.";
        if (placeInCurrentChapter && ctx.CurrentChapterId is { } activeChapterId)
        {
            var chapter = await chapters.GetAsync(activeChapterId);
            if (chapter is null || chapter.ProjectId != ctx.ProjectId)
                return $"Error: chapter {activeChapterId} not found in this project.";
            if (chapter.VisualMode == ChapterVisualMode.Prose)
                return "Error: placeInCurrentChapter requires the current chapter to already be IllustratedProse or PicturePage. Call set_chapter_visual_mode first.";
        }
        if (placeInCurrentChapter
            && targetChapterId is { } requestedTargetChapterId
            && ctx.CurrentChapterId is { } currentChapterId
            && requestedTargetChapterId != currentChapterId)
        {
            return "Error: targetChapterId must be the current chapter when placeInCurrentChapter is true.";
        }

        var effectiveTargetChapterId = targetChapterId
            ?? (placeInCurrentChapter ? ctx.CurrentChapterId : null);
        var targetResolution = await ResolvePicturePageGenerationTargetAsync(ctx, effectiveTargetChapterId, targetPictureImageElementId);
        if (targetResolution.Error is not null)
            return targetResolution.Error;

        var promptText = targetResolution.PromptAppendix is { Length: > 0 } appendix
            ? prompt.Trim() + appendix
            : prompt.Trim();
        var effectiveSize = string.IsNullOrWhiteSpace(size) && targetResolution.Target is { } target
            ? target.RecommendedSize
            : string.IsNullOrWhiteSpace(size) ? "auto" : size.Trim();

        var job = await imageJobs.CreateGenerateJobAsync(ctx.ProjectId, new ProjectImageGenerateJobRequest(
            promptText,
            effectiveSize,
            string.IsNullOrWhiteSpace(quality) ? "auto" : quality.Trim(),
            string.IsNullOrWhiteSpace(outputFormat) ? "png" : outputFormat.Trim(),
            outputCompression,
            altText?.Trim() ?? string.Empty,
            1,
            (referenceImageIds ?? []).Distinct().ToList(),
            Label: "Editor chat image",
            EntityTargets: ctx.OutlineStaging is null ? entityTargets : null));
        ctx.TrackImageGenerationJob(job.Id);

        await imageRuntime.EnqueueProjectAsync(ctx.ProjectId, CancellationToken.None);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(imageOptions.Value.AgentJobWaitTimeoutSeconds, 1, 3600));
        var completed = await imageRuntime.WaitForJobCompletionAsync(job.Id, timeout, CancellationToken.None);
        job = await imageJobs.GetJobAsync(ctx.ProjectId, job.Id, CancellationToken.None)
            ?? throw new InvalidOperationException($"Image generation job {job.Id:N} was not found after queueing.");

        var outputImages = new List<ProjectImageView>();
        foreach (var imageId in job.OutputImageIds)
        {
            if (await projectImages.GetAsync(ctx.ProjectId, imageId, CancellationToken.None) is { } image)
            {
                outputImages.Add(image);
                ctx.AddVisual(await BuildVisualAsync(
                    ctx,
                    image,
                    title: image.FileName,
                    caption: "Generated output saved to the image library."));
                ctx.AddModelOnlyImage(image);
                foreach (var entityTarget in (entityTargets ?? []).Where(item => item.EntityId != Guid.Empty).DistinctBy(item => item.EntityId))
                {
                    if (ctx.OutlineStaging is null)
                    {
                        await entityVisualExamples.AttachAsync(ctx.ProjectId, entityTarget.EntityId, image.Id, entityTarget.Label, EntityVisualExampleOrigin.Agent);
                    }
                    else
                    {
                        var after = new EntityVisualChange("attach", EntityId: entityTarget.EntityId, ImageId: image.Id, Label: entityTarget.Label);
                        await ctx.OutlineStaging.StageExternalChangeAsync(
                            $"Attach generated image to entity {entityTarget.EntityId:N}", null, after,
                            new { status = "staged", entityTarget.EntityId, imageId = image.Id, entityTarget.Label },
                            "EntityVisualExample", $"{entityTarget.EntityId:N}/{image.Id:N}");
                    }
                }
            }
        }

        object? placement = null;
        if (placeInCurrentChapter && outputImages.FirstOrDefault() is { } placedImage)
        {
            var chapterId = ctx.CurrentChapterId!.Value;
            var placed = await chapterVisuals.AddImageToChapterAsync(ctx.ProjectId, chapterId, placedImage.Id);
            placement = new
            {
                chapterId,
                elementId = placed.ElementId,
                placed.State.VisualMode,
                visualLayout = await VisualStatePayloadObjectAsync(ctx, placed.State, "Image placed in chapter.", placed.ElementId),
            };
        }

        ctx.OnMutated();
        return JsonSerializer.Serialize(new
        {
            completed,
            job = new
            {
                job.Id,
                job.Kind,
                job.Status,
                job.Label,
                job.Size,
                job.Quality,
                job.OutputFormat,
                job.Count,
                job.OutputImageIds,
                job.OutputStates,
                job.OutputErrors,
                job.Error,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
            },
            images = outputImages.Select(image => new
            {
                image.Id,
                image.FileName,
                image.ContentType,
                image.PreviewUrl,
                image.AltText,
                image.Source,
                image.Prompt,
                image.GenerationModel,
                image.CreatedAt,
                image.UpdatedAt,
                image.SizeBytes,
            }),
            imageGenerationTarget = targetResolution.Target is null
                ? null
                : PicturePageImageGenerationGuidance.DescribeTarget(targetResolution.Target),
            placement,
            note = completed ? null : "Timed out waiting for the image job. The Images tab will continue showing progress.",
        });
    }

    private async Task<ImageGenerationTargetResolution> ResolvePicturePageGenerationTargetAsync(
        EditorChatContext ctx,
        Guid? targetChapterId,
        Guid? targetPictureImageElementId)
    {
        if (targetChapterId is null || targetChapterId == Guid.Empty)
        {
            return targetPictureImageElementId is { } elementId && elementId != Guid.Empty
                ? new ImageGenerationTargetResolution(null, string.Empty, "Error: targetPictureImageElementId requires targetChapterId.")
                : new ImageGenerationTargetResolution(null, string.Empty, Error: null);
        }

        var chapter = await chapters.GetAsync(targetChapterId.Value);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return new ImageGenerationTargetResolution(null, string.Empty, $"Error: chapter {targetChapterId.Value:N} not found in this project.");

        var state = await chapterVisuals.GetAsync(chapter.Id);
        if (state is null)
            return new ImageGenerationTargetResolution(null, string.Empty, $"Error: visual layout for chapter {chapter.Id:N} was not found.");

        if (!PicturePageImageGenerationGuidance.TryResolveTarget(state, targetPictureImageElementId, out var target, out var error))
            return new ImageGenerationTargetResolution(null, string.Empty, error);

        var appendix = PicturePageImageGenerationGuidance.BuildPromptAppendix(target!, state.PageLayout.TextElements);
        return new ImageGenerationTargetResolution(target, appendix, Error: null);
    }

    private async Task<string> AddProjectImageToChapterAsync(EditorChatContext ctx, Guid chapterId, Guid imageId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";
        if (chapter.VisualMode == ChapterVisualMode.Prose)
            return "Error: add_project_image_to_chapter requires an IllustratedProse or PicturePage chapter. Call set_chapter_visual_mode first.";

        var result = await chapterVisuals.AddImageToChapterAsync(ctx.ProjectId, chapterId, imageId);
        ctx.OnMutated();
        return await VisualStatePayloadAsync(ctx, result.State, "Image placed in chapter.", result.ElementId);
    }

    private async Task<string> UpdateIllustratedProseImageAsync(
        EditorChatContext ctx,
        Guid chapterId,
        Guid imageBlockId,
        string? anchorPosition,
        int? paragraphIndex,
        double? widthPercent,
        string? alignment,
        string? caption,
        string? altTextOverride,
        int? sortOrder,
        bool? startOnNewPage)
    {
        var resolved = await RequireVisualModeAsync(ctx, chapterId, ChapterVisualMode.IllustratedProse);
        if (resolved.Error is not null) return resolved.Error;

        var state = resolved.State!;
        var block = state.IllustrationLayout.Images.FirstOrDefault(candidate => candidate.Id == imageBlockId);
        if (block is null)
            return $"Error: illustrated prose image block {imageBlockId} was not found in chapter {chapterId}.";

        if (!TryParseOptionalEnum(anchorPosition, out ChapterImageAnchorPosition? parsedAnchor, out var anchorError))
            return anchorError!;
        if (!TryParseOptionalEnum(alignment, out ChapterImageAlignment? parsedAlignment, out var alignmentError))
            return alignmentError!;

        var movedAnchor = parsedAnchor is not null || paragraphIndex is not null;
        var updatedBlock = block with
        {
            AnchorPosition = parsedAnchor ?? block.AnchorPosition,
            ParagraphIndex = paragraphIndex ?? block.ParagraphIndex,
            ParagraphHash = movedAnchor ? string.Empty : block.ParagraphHash,
            WidthPercent = widthPercent ?? block.WidthPercent,
            Alignment = parsedAlignment ?? block.Alignment,
            Caption = caption ?? block.Caption,
            AltTextOverride = altTextOverride ?? block.AltTextOverride,
            SortOrder = sortOrder ?? block.SortOrder,
            StartOnNewPage = startOnNewPage ?? block.StartOnNewPage,
        };

        var updated = await chapterVisuals.SaveIllustrationLayoutAsync(
            chapterId,
            state.IllustrationLayout with
            {
                Images = state.IllustrationLayout.Images
                    .Select(candidate => candidate.Id == imageBlockId ? updatedBlock : candidate)
                    .ToList(),
            });

        ctx.OnMutated();
        return await VisualStatePayloadAsync(ctx, updated, "Illustrated prose image updated.", imageBlockId);
    }

    private async Task<string> RemoveIllustratedProseImageAsync(EditorChatContext ctx, Guid chapterId, Guid imageBlockId)
    {
        var resolved = await RequireVisualModeAsync(ctx, chapterId, ChapterVisualMode.IllustratedProse);
        if (resolved.Error is not null) return resolved.Error;

        var state = resolved.State!;
        if (state.IllustrationLayout.Images.All(candidate => candidate.Id != imageBlockId))
            return $"Error: illustrated prose image block {imageBlockId} was not found in chapter {chapterId}.";

        var updated = await chapterVisuals.SaveIllustrationLayoutAsync(
            chapterId,
            state.IllustrationLayout with
            {
                Images = state.IllustrationLayout.Images.Where(candidate => candidate.Id != imageBlockId).ToList(),
            });

        ctx.OnMutated();
        return await VisualStatePayloadAsync(ctx, updated, "Illustrated prose image removed.", imageBlockId);
    }

    private async Task<string> UpdatePicturePageImageAsync(
        EditorChatContext ctx,
        Guid chapterId,
        Guid imageElementId,
        double? xPercent,
        double? yPercent,
        double? widthPercent,
        double? heightPercent,
        string? fit,
        double? opacity,
        int? zIndex,
        string? altTextOverride)
    {
        var resolved = await RequireVisualModeAsync(ctx, chapterId, ChapterVisualMode.PicturePage);
        if (resolved.Error is not null) return resolved.Error;

        var state = resolved.State!;
        var image = state.PageLayout.Images.FirstOrDefault(candidate => candidate.Id == imageElementId);
        if (image is null)
            return $"Error: picture page image element {imageElementId} was not found in chapter {chapterId}.";

        if (!TryParseOptionalEnum(fit, out ChapterImageFit? parsedFit, out var fitError))
            return fitError!;

        var updatedImage = image with
        {
            XPercent = xPercent ?? image.XPercent,
            YPercent = yPercent ?? image.YPercent,
            WidthPercent = widthPercent ?? image.WidthPercent,
            HeightPercent = heightPercent ?? image.HeightPercent,
            Fit = parsedFit ?? image.Fit,
            Opacity = opacity ?? image.Opacity,
            ZIndex = zIndex ?? image.ZIndex,
            AltTextOverride = altTextOverride ?? image.AltTextOverride,
        };

        var updated = await chapterVisuals.SavePageLayoutAsync(
            chapterId,
            state.PageLayout with
            {
                Images = state.PageLayout.Images
                    .Select(candidate => candidate.Id == imageElementId ? updatedImage : candidate)
                    .ToList(),
            });

        ctx.OnMutated();
        return await VisualStatePayloadAsync(ctx, updated, "Picture page image updated.", imageElementId);
    }

    private async Task<string> UpsertPicturePageTextAsync(
        EditorChatContext ctx,
        Guid chapterId,
        Guid? textElementId,
        string? text,
        double? xPercent,
        double? yPercent,
        double? widthPercent,
        double? heightPercent,
        int? zIndex,
        int? readingOrder,
        string? fontFamily,
        double? fontSizePercent,
        double? lineHeight,
        string? color,
        string? backgroundColor,
        double? backgroundOpacity,
        string? textAlign,
        string? verticalAlign,
        string? shadow)
    {
        var resolved = await RequireVisualModeAsync(ctx, chapterId, ChapterVisualMode.PicturePage);
        if (resolved.Error is not null) return resolved.Error;

        if (!TryParseOptionalEnum(fontFamily, out PicturePageFontFamily? parsedFontFamily, out var fontError))
            return fontError!;
        if (!TryParseOptionalEnum(textAlign, out PicturePageTextAlign? parsedTextAlign, out var alignError))
            return alignError!;
        if (!TryParseOptionalEnum(verticalAlign, out ChapterTextVerticalAlign? parsedVerticalAlign, out var verticalError))
            return verticalError!;
        if (!TryParseOptionalEnum(shadow, out PicturePageTextShadow? parsedShadow, out var shadowError))
            return shadowError!;

        var state = resolved.State!;
        PicturePageTextElement? existing = null;
        if (textElementId is Guid requestedId && requestedId != Guid.Empty)
            existing = state.PageLayout.TextElements.FirstOrDefault(candidate => candidate.Id == requestedId);
        if (textElementId is Guid requestedTextId && requestedTextId != Guid.Empty && existing is null)
            return $"Error: picture page text element {requestedTextId} was not found in chapter {chapterId}.";
        if (existing is null && string.IsNullOrWhiteSpace(text))
            return "Error: text is required when creating a new PicturePage text element.";

        var elementId = existing?.Id ?? Guid.NewGuid();
        var maxZ = state.PageLayout.Images.Select(image => image.ZIndex)
            .Concat(state.PageLayout.TextElements.Select(textElement => textElement.ZIndex))
            .DefaultIfEmpty(0)
            .Max();
        var maxReadingOrder = state.PageLayout.TextElements.Select(textElement => textElement.ReadingOrder)
            .DefaultIfEmpty(-1)
            .Max();

        var updatedText = new PicturePageTextElement(
            elementId,
            text ?? existing?.Text ?? string.Empty,
            xPercent ?? existing?.XPercent ?? 12,
            yPercent ?? existing?.YPercent ?? 70,
            widthPercent ?? existing?.WidthPercent ?? 76,
            heightPercent ?? existing?.HeightPercent ?? 16,
            zIndex ?? existing?.ZIndex ?? maxZ + 1,
            readingOrder ?? existing?.ReadingOrder ?? maxReadingOrder + 1,
            parsedFontFamily ?? existing?.FontFamily ?? PicturePageFontFamily.Serif,
            fontSizePercent ?? existing?.FontSizePercent ?? 4.5,
            lineHeight ?? existing?.LineHeight ?? 1.25,
            color ?? existing?.Color ?? "#111827",
            backgroundColor ?? existing?.BackgroundColor ?? "#FFFFFF",
            backgroundOpacity ?? existing?.BackgroundOpacity ?? 0,
            parsedTextAlign ?? existing?.TextAlign ?? PicturePageTextAlign.Center,
            parsedVerticalAlign ?? existing?.VerticalAlign ?? ChapterTextVerticalAlign.Middle,
            parsedShadow ?? existing?.Shadow ?? PicturePageTextShadow.None);

        var updated = await chapterVisuals.SavePageLayoutAsync(
            chapterId,
            state.PageLayout with
            {
                TextElements = existing is null
                    ? state.PageLayout.TextElements.Append(updatedText).ToList()
                    : state.PageLayout.TextElements
                        .Select(candidate => candidate.Id == elementId ? updatedText : candidate)
                        .ToList(),
            });

        ctx.OnMutated();
        return await VisualStatePayloadAsync(ctx, updated, existing is null ? "Picture page text created." : "Picture page text updated.", elementId);
    }

    private async Task<string> RemovePicturePageElementAsync(
        EditorChatContext ctx,
        Guid chapterId,
        string elementKind,
        Guid elementId)
    {
        var resolved = await RequireVisualModeAsync(ctx, chapterId, ChapterVisualMode.PicturePage);
        if (resolved.Error is not null) return resolved.Error;

        var state = resolved.State!;
        if (string.Equals(elementKind, "image", StringComparison.OrdinalIgnoreCase))
        {
            if (state.PageLayout.Images.All(candidate => candidate.Id != elementId))
                return $"Error: picture page image element {elementId} was not found in chapter {chapterId}.";

            var updated = await chapterVisuals.SavePageLayoutAsync(
                chapterId,
                state.PageLayout with { Images = state.PageLayout.Images.Where(candidate => candidate.Id != elementId).ToList() });
            ctx.OnMutated();
            return await VisualStatePayloadAsync(ctx, updated, "Picture page image removed.", elementId);
        }

        if (string.Equals(elementKind, "text", StringComparison.OrdinalIgnoreCase))
        {
            if (state.PageLayout.TextElements.All(candidate => candidate.Id != elementId))
                return $"Error: picture page text element {elementId} was not found in chapter {chapterId}.";

            var updated = await chapterVisuals.SavePageLayoutAsync(
                chapterId,
                state.PageLayout with { TextElements = state.PageLayout.TextElements.Where(candidate => candidate.Id != elementId).ToList() });
            ctx.OnMutated();
            return await VisualStatePayloadAsync(ctx, updated, "Picture page text removed.", elementId);
        }

        return "Error: elementKind must be image or text.";
    }

    private async Task<string> AddProjectImageToContextAsync(EditorChatContext ctx, Guid chapterId, Guid imageId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";
        if (await projectImages.GetAsync(ctx.ProjectId, imageId) is null)
            return $"Error: image {imageId} not found in this project.";

        await editorContext.SetItemIncludedAsync(
            ctx.ProjectId,
            chapterId,
            ContextItemKind.ProjectImage,
            EditorContextKeys.ProjectImage(imageId),
            isIncluded: true);
        ctx.OnMutated();
        return "Image added to chapter context.";
    }

    private async Task<string> RemoveProjectImageFromContextAsync(EditorChatContext ctx, Guid chapterId, Guid imageId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        await editorContext.SetItemIncludedAsync(
            ctx.ProjectId,
            chapterId,
            ContextItemKind.ProjectImage,
            EditorContextKeys.ProjectImage(imageId),
            isIncluded: false);
        ctx.OnMutated();
        return "Image removed from chapter context.";
    }

    private async Task<VisualModeResolution> RequireVisualModeAsync(
        EditorChatContext ctx,
        Guid chapterId,
        ChapterVisualMode requiredMode)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return VisualModeResolution.Fail($"Error: chapter {chapterId} not found in this project.");
        if (chapter.VisualMode != requiredMode)
        {
            return VisualModeResolution.Fail(
                $"Error: this tool requires a {requiredMode} chapter, but '{chapter.Title}' is {chapter.VisualMode}. Call set_chapter_visual_mode first or choose the correct tool family.");
        }

        var state = await chapterVisuals.GetAsync(chapterId);
        return state is null
            ? VisualModeResolution.Fail($"Error: visual layout for chapter {chapterId} was not found.")
            : new VisualModeResolution(state, Error: null);
    }

    private async Task<string> VisualStatePayloadAsync(
        EditorChatContext ctx,
        ChapterVisualState state,
        string message,
        Guid? elementId = null) =>
        JsonSerializer.Serialize(await VisualStatePayloadObjectAsync(ctx, state, message, elementId));

    private async Task<object> VisualStatePayloadObjectAsync(
        EditorChatContext ctx,
        ChapterVisualState state,
        string message,
        Guid? elementId = null)
    {
        var imageNames = (await projectImages.ListAsync(ctx.ProjectId))
            .ToDictionary(image => image.Id, image => image.FileName);
        var chapter = state.VisualMode == ChapterVisualMode.PicturePage
            ? await chapters.GetAsync(state.ChapterId)
            : null;

        return new
        {
            message,
            chapterId = state.ChapterId,
            elementId,
            state.VisualMode,
            state.PageLayoutKind,
            state.IllustrationLayout,
            state.PageLayout,
            projectedBody = state.VisualMode == ChapterVisualMode.PicturePage ? chapter?.Body ?? string.Empty : null,
            manifest = chapterVisuals.BuildManifest(state, imageNames),
        };
    }

    private static bool TryParseOptionalEnum<TEnum>(string? value, out TEnum? parsed, out string? error)
        where TEnum : struct, Enum
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var candidate)
            && Enum.IsDefined(candidate))
        {
            parsed = candidate;
            return true;
        }

        error = $"Error: {typeof(TEnum).Name} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.";
        return false;
    }

    private sealed record VisualModeResolution(ChapterVisualState? State, string? Error)
    {
        public static VisualModeResolution Fail(string error) => new(null, error);
    }

    private int EffectiveReadChapterPageMaxChars() => Math.Max(256, editorOptions.Value.ReadChapterPageMaxChars);

    private static int CountReadChapterPages(string body, int pageMaxChars)
    {
        var lines = ChapterFormatting.SplitLines(body);
        return lines.Count == 0
            ? 1
            : BuildReadChapterPages(lines, pageMaxChars).Count;
    }

    private static List<ReadChapterPage> BuildReadChapterPages(
        IReadOnlyList<string> lines,
        int pageMaxChars)
    {
        var pages = new List<ReadChapterPage>();
        var currentPage = new ReadChapterPage();
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var prefixLength = lineNumberWidth + 2;

        for (var lineNumber = 1; lineNumber <= lines.Count; lineNumber++)
        {
            var text = lines[lineNumber - 1];
            var fullLineLength = prefixLength + text.Length;
            if (fullLineLength <= pageMaxChars)
            {
                var addLength = fullLineLength + (currentPage.Segments.Count == 0 ? 0 : 1);
                if (currentPage.Segments.Count > 0 && currentPage.ContentCharCount + addLength > pageMaxChars)
                {
                    pages.Add(currentPage);
                    currentPage = new ReadChapterPage();
                }

                currentPage.Add(new ReadChapterLineSegment(
                    lineNumber,
                    text,
                    StartColumn: 1,
                    EndColumn: text.Length,
                    LineLength: text.Length,
                    IsFullLine: true), lineNumberWidth);
                continue;
            }

            if (currentPage.Segments.Count > 0)
            {
                pages.Add(currentPage);
                currentPage = new ReadChapterPage();
            }

            var maxTextChars = Math.Max(1, pageMaxChars - prefixLength);
            for (var offset = 0; offset < text.Length; offset += maxTextChars)
            {
                var length = Math.Min(maxTextChars, text.Length - offset);
                var segment = new ReadChapterLineSegment(
                    lineNumber,
                    text.Substring(offset, length),
                    StartColumn: offset + 1,
                    EndColumn: offset + length,
                    LineLength: text.Length,
                    IsFullLine: false);

                var partialPage = new ReadChapterPage();
                partialPage.Add(segment, lineNumberWidth);
                pages.Add(partialPage);
            }
        }

        if (currentPage.Segments.Count > 0)
            pages.Add(currentPage);

        return pages;
    }

    private static string FormatReadChapterPageContent(ReadChapterPage page, int lineNumberWidth)
    {
        var sb = new StringBuilder(page.ContentCharCount);
        for (var i = 0; i < page.Segments.Count; i++)
        {
            var segment = page.Segments[i];
            sb.Append(segment.LineNumber.ToString().PadLeft(lineNumberWidth, '0'));
            sb.Append(": ");
            sb.Append(segment.Text);
            if (i < page.Segments.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    private sealed record ReadChapterLineSegment(
        int LineNumber,
        string Text,
        int StartColumn,
        int EndColumn,
        int LineLength,
        bool IsFullLine);

    private sealed class ReadChapterPage
    {
        public List<ReadChapterLineSegment> Segments { get; } = [];
        public int ContentCharCount { get; private set; }

        public int PageStartLine => Segments[0].LineNumber;
        public int PageEndLine => Segments[^1].LineNumber;
        public int PageStartColumn => Segments[0].StartColumn;
        public int PageEndColumn => Segments[^1].EndColumn;
        public bool StartsInsideLine => Segments[0].StartColumn > 1;
        public bool EndsInsideLine => Segments[^1].EndColumn < Segments[^1].LineLength;
        public bool ContainsPartialLine => Segments.Any(segment => !segment.IsFullLine);

        public void Add(ReadChapterLineSegment segment, int lineNumberWidth)
        {
            if (Segments.Count > 0) ContentCharCount++;
            ContentCharCount += lineNumberWidth + 2 + segment.Text.Length;
            Segments.Add(segment);
        }
    }

    private static string BuildEditChapterResult(
        string summary,
        string newBody,
        int? affectedStartLine,
        int? affectedEndLine,
        int anchorLine,
        string anchorDescription)
    {
        var newLines = ChapterFormatting.SplitLines(newBody);
        var (snippetStartLine, snippetEndLine) = ResolveEditChapterSnippetRange(
            newLines.Count,
            affectedStartLine,
            affectedEndLine,
            anchorLine);

        var sb = new StringBuilder();
        sb.Append("OK. ").AppendLine(summary);
        sb.AppendLine();

        if (affectedStartLine is int startLine && affectedEndLine is int endLine)
        {
            sb.Append("Affected new lines: ")
              .Append(startLine)
              .Append('-')
              .Append(endLine)
              .AppendLine(".");
        }
        else
        {
            sb.Append("Affected new lines: none; deletion/empty-edit anchor: ")
              .Append(anchorDescription)
              .AppendLine(".");
        }

        if (snippetStartLine == 0)
        {
            sb.AppendLine("Returned excerpt lines: none.");
            sb.AppendLine();
            sb.AppendLine("New body excerpt:");
            sb.Append("(empty)");
            return sb.ToString();
        }

        sb.Append("Returned excerpt lines: ")
          .Append(snippetStartLine)
          .Append('-')
          .Append(snippetEndLine)
          .AppendLine(".");
        sb.AppendLine();
        sb.AppendLine("New body excerpt:");
        sb.Append(FormatNumberedLines(newLines, snippetStartLine, snippetEndLine));
        return sb.ToString();
    }

    private static (int StartLine, int EndLine) ResolveEditChapterSnippetRange(
        int lineCount,
        int? affectedStartLine,
        int? affectedEndLine,
        int anchorLine)
    {
        if (lineCount == 0)
            return (0, 0);

        if (affectedStartLine is int startLine && affectedEndLine is int endLine)
        {
            return (
                Math.Max(1, startLine - EditChapterExcerptContextLines),
                Math.Min(lineCount, endLine + EditChapterExcerptContextLines));
        }

        var clampedAnchorLine = Math.Clamp(anchorLine, 1, lineCount + 1);
        if (clampedAnchorLine > lineCount)
            return (Math.Max(1, lineCount - EditChapterExcerptContextLines + 1), lineCount);

        return (
            Math.Max(1, clampedAnchorLine - EditChapterExcerptContextLines),
            Math.Min(lineCount, clampedAnchorLine + EditChapterExcerptContextLines - 1));
    }

    private static string FormatNumberedLines(IReadOnlyList<string> lines, int startLine, int endLine)
    {
        var lineNumberWidth = Math.Max(4, lines.Count.ToString().Length);
        var sb = new StringBuilder();
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            sb.Append(lineNumber.ToString().PadLeft(lineNumberWidth, '0'));
            sb.Append(": ");
            sb.Append(lines[lineNumber - 1]);
            if (lineNumber < endLine) sb.Append('\n');
        }

        return sb.ToString();
    }

    private async Task<string> EditChapterAsync(
        EditorChatContext ctx,
        Guid chapterId,
        string content,
        int? startLine,
        int? endLine)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";
        if (chapter.VisualMode == ChapterVisualMode.PicturePage)
            return "Error: edit_chapter cannot edit PicturePage chapters because their body is projected from layout text boxes. Use upsert_picture_page_text or other Picture Page visual layout tools.";

        content ??= string.Empty;
        var existingBody = chapter.Body;
        if (ctx.ReviewEdits && ctx.EditorStaging?.TryGetChapterBodyDraft(chapter.Id, out var draftBody) == true)
            existingBody = draftBody;

        var existingLines = ChapterFormatting.SplitLines(existingBody);
        var contentLines = ChapterFormatting.SplitLines(content);

        string newBody;
        string summary;
        int? affectedStartLine = null;
        int? affectedEndLine = null;
        var anchorLine = 1;
        var anchorDescription = "chapter start";

        string InsertBeforeLine(int insertLine)
        {
            var merged = new List<string>(existingLines.Count + contentLines.Count);
            merged.AddRange(existingLines.Take(insertLine - 1));
            merged.AddRange(contentLines);
            merged.AddRange(existingLines.Skip(insertLine - 1));
            return ChapterFormatting.JoinLines(merged);
        }

        string InsertSummary(int insertLine) => insertLine == existingLines.Count + 1
            ? existingLines.Count == 0
                ? $"Inserted {contentLines.Count} line(s) into the empty chapter."
                : $"Appended {contentLines.Count} line(s) after line {existingLines.Count}."
            : $"Inserted {contentLines.Count} line(s) before line {insertLine}.";

        if (startLine is null && endLine is null)
        {
            var appendLine = existingLines.Count + 1;
            newBody = InsertBeforeLine(appendLine);
            summary = existingLines.Count == 0
                ? $"Appended {contentLines.Count} line(s) to the empty chapter."
                : $"Appended {contentLines.Count} line(s) after line {existingLines.Count}.";
            if (contentLines.Count > 0)
            {
                affectedStartLine = appendLine;
                affectedEndLine = appendLine + contentLines.Count - 1;
            }
            else
            {
                anchorLine = appendLine;
                anchorDescription = existingLines.Count == 0
                    ? "chapter remains empty"
                    : $"after line {existingLines.Count}";
            }
        }
        else if (startLine is int insertLine && endLine is null)
        {
            if (insertLine < 1 || insertLine > existingLines.Count + 1)
                return $"Error: startLine {insertLine} out of range (1..{existingLines.Count + 1}).";

            newBody = InsertBeforeLine(insertLine);
            summary = InsertSummary(insertLine);
            if (contentLines.Count > 0)
            {
                affectedStartLine = insertLine;
                affectedEndLine = insertLine + contentLines.Count - 1;
            }
            else
            {
                anchorLine = insertLine;
                anchorDescription = insertLine == existingLines.Count + 1
                    ? existingLines.Count == 0 ? "chapter remains empty" : $"after line {existingLines.Count}"
                    : $"before line {insertLine}";
            }
        }
        else if (startLine is int replaceStart && endLine is int replaceEnd)
        {
            if (existingLines.Count == 0 && replaceStart == 1 && replaceEnd == 1)
            {
                newBody = ChapterFormatting.JoinLines(contentLines);
                summary = $"Wrote {contentLines.Count} line(s) into the empty chapter.";
                if (contentLines.Count > 0)
                {
                    affectedStartLine = 1;
                    affectedEndLine = contentLines.Count;
                }
                else
                {
                    anchorLine = 1;
                    anchorDescription = "chapter is empty";
                }
            }
            else
            {
                if (replaceStart < 1 || replaceStart > existingLines.Count)
                    return $"Error: startLine {replaceStart} out of range (1..{existingLines.Count}).";
                if (replaceEnd < replaceStart || replaceEnd > existingLines.Count)
                    return $"Error: endLine {replaceEnd} out of range ({replaceStart}..{existingLines.Count}).";

                var replacedCount = replaceEnd - replaceStart + 1;
                var merged = new List<string>(existingLines.Count - replacedCount + contentLines.Count);
                merged.AddRange(existingLines.Take(replaceStart - 1));
                merged.AddRange(contentLines);
                merged.AddRange(existingLines.Skip(replaceEnd));
                newBody = ChapterFormatting.JoinLines(merged);
                summary = replaceStart == 1 && replaceEnd == existingLines.Count
                    ? $"Full rewrite ({existingLines.Count} -> {contentLines.Count} lines)."
                    : $"Replaced lines {replaceStart}-{replaceEnd} ({replacedCount} -> {contentLines.Count} lines).";
                if (contentLines.Count > 0)
                {
                    affectedStartLine = replaceStart;
                    affectedEndLine = replaceStart + contentLines.Count - 1;
                }
                else
                {
                    var newLineCount = existingLines.Count - replacedCount;
                    anchorLine = replaceStart;
                    anchorDescription = newLineCount == 0
                        ? "chapter is now empty"
                        : replaceStart <= newLineCount
                            ? $"before line {replaceStart}"
                            : $"after line {newLineCount}";
                }
            }
        }
        else
        {
            return "Error: endLine provided without startLine.";
        }

        var result = BuildEditChapterResult(summary, newBody, affectedStartLine, affectedEndLine, anchorLine, anchorDescription);

        if (ctx.ReviewEdits && ctx.EditorStaging is not null && !ctx.ShouldBypassReviewForChapterBody(chapter))
        {
            await ctx.EditorStaging.StageChapterBodyEditAsync(chapter, existingBody, newBody, summary, result);
            return result;
        }

        await chapters.UpdateAsync(chapterId, body: newBody);
        if (ctx.ReviewEdits)
            ctx.MarkChapterBodyDirectlyEdited(chapterId);
        ctx.OnMutated();
        return result;
    }

    private async Task<string> StartContestAsync(
        EditorChatContext ctx,
        Guid chapterId)
    {
        if (chapterId == Guid.Empty)
            return "Error: chapterId is required.";

        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";
        if (chapter.VisualMode == ChapterVisualMode.PicturePage)
            return "Error: start_contest cannot target PicturePage chapters because contest candidates mutate chapter prose. Use Picture Page visual layout tools instead.";

        ctx.RequestContest(new EditorContestStartRequest(chapterId));

        return "Contest started. Candidate responses will stream into the review modal.";
    }

    private async Task<IReadOnlyList<OrderedChapter>> ListOrderedChaptersAsync(Guid projectId)
    {
        var actList = await acts.ListAsync(projectId);
        var allChapters = await chapters.ListAsync(projectId);
        var ordered = new List<OrderedChapter>();

        foreach (var act in actList.OrderBy(act => act.Order))
        {
            foreach (var chapter in allChapters
                .Where(chapter => chapter.ActId == act.Id)
                .OrderBy(chapter => chapter.Order))
            {
                ordered.Add(new OrderedChapter(chapter, ordered.Count));
            }
        }

        foreach (var chapter in allChapters
            .Where(chapter => chapter.ActId is null)
            .OrderBy(chapter => chapter.Order))
        {
            ordered.Add(new OrderedChapter(chapter, ordered.Count));
        }

        return ordered;
    }

    private async Task ScoreProjectSearchHitsAsync(
        Guid projectId,
        IReadOnlyList<string> terms,
        IReadOnlyDictionary<Guid, ImpactCandidate> candidates)
    {
        try
        {
            var queryText = string.Join(' ', terms);
            var results = await projectSearch.SearchAsync(new ProjectSearchRequest(
                projectId,
                queryText,
                Math.Min(50, Math.Max(12, candidates.Count)),
                [ProjectSearchSourceTypes.Chapter, ProjectSearchSourceTypes.ContextChapter]));

            foreach (var result in results)
            {
                if (result.SourceId is not Guid chapterId
                    || !candidates.TryGetValue(chapterId, out var candidate))
                {
                    continue;
                }

                var score = result.Reasons.Contains("semantic")
                    ? 42
                    : 30;
                candidate.Add(score, "project-search", $"Chapter/context search hit: {result.Metadata ?? result.Title}.");
                candidate.AddEvidence("project-search", Truncate(result.Content, 420));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var candidate in candidates.Values.Take(1))
                candidate.AddEvidence("project-search-warning", $"Project search was unavailable: {ex.Message}");
        }
    }

    private async Task ScoreEntityAnchorsAsync(
        Guid projectId,
        IReadOnlyList<Guid>? entityIds,
        string reasonKind,
        IReadOnlyDictionary<Guid, ImpactCandidate> candidates)
    {
        if (entityIds is null || entityIds.Count == 0) return;

        foreach (var entityId in entityIds.Distinct())
        {
            var entity = await entities.GetAsync(projectId, entityId);
            if (entity is null) continue;

            if (entity.ParentId is { } parentId && candidates.TryGetValue(parentId, out var parentCandidate))
            {
                parentCandidate.Add(38, reasonKind, $"{entity.Type} '{entity.Name}' is scoped to this chapter.");
                parentCandidate.AddEvidence(reasonKind, $"{entity.Type}: {entity.Name}");
            }

            foreach (var link in await entities.ListLinksAsync(projectId, entityId))
            {
                if (candidates.TryGetValue(link.OtherEntityId, out var chapterCandidate))
                {
                    chapterCandidate.Add(34, reasonKind, $"{entity.Type} '{entity.Name}' links to this chapter via {link.EdgeType}.");
                    chapterCandidate.AddEvidence(reasonKind, $"{entity.Name} {link.EdgeType} {link.OtherEntityName}");
                    continue;
                }

                if (string.Equals(link.OtherEntityType, EntityTypeService.EventNodeType, StringComparison.OrdinalIgnoreCase))
                {
                    var linkedEvent = await entities.GetAsync(projectId, link.OtherEntityId);
                    if (linkedEvent?.ParentId is { } eventChapterId && candidates.TryGetValue(eventChapterId, out var eventCandidate))
                    {
                        eventCandidate.Add(30, reasonKind, $"{entity.Type} '{entity.Name}' links to event '{linkedEvent.Name}' in this chapter.");
                        eventCandidate.AddEvidence(reasonKind, $"{entity.Name} {link.EdgeType} {linkedEvent.Name}");
                    }
                }
            }
        }
    }

    private static IReadOnlyList<string> BuildImpactTerms(string query, IReadOnlyList<string>? keywords)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
            terms.AddRange(SearchTerms(query));
        if (keywords is not null)
            terms.AddRange(keywords.SelectMany(SearchTerms));

        return terms
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
    }

    private static void ScoreText(ImpactCandidate candidate, string source, string? text, IReadOnlyList<string> terms, int maxScore)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var matches = terms
            .Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        if (matches.Count == 0) return;

        var score = Math.Min(maxScore, matches.Count * 8);
        candidate.Add(score, source, $"{source} matched: {string.Join(", ", matches)}.");
        candidate.AddEvidence(source, ExtractEvidence(text, matches[0]));
    }

    private static string ExtractEvidence(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return Truncate(text, 240);

        var start = Math.Max(0, index - 120);
        var length = Math.Min(text.Length - start, term.Length + 240);
        var snippet = text.Substring(start, length).ReplaceLineEndings(" ");
        if (start > 0) snippet = "..." + snippet;
        if (start + length < text.Length) snippet += "...";
        return snippet;
    }

    private async Task<EditorChatVisualAttachment> BuildVisualAsync(
        EditorChatContext ctx,
        ProjectImageView image,
        string? title = null,
        string? caption = null)
    {
        var data = await projectImages.GetDataAsync(ctx.ProjectId, image.Id, maxEdge: null, CancellationToken.None);
        var size = data is null ? (Width: (int?)null, Height: (int?)null) : ReadSize(data.Data);
        return new EditorChatVisualAttachment(
            Guid.NewGuid(),
            title ?? image.FileName,
            caption ?? (string.IsNullOrWhiteSpace(image.Prompt) ? image.Source.ToString() : Truncate(image.Prompt, 120)),
            image.PreviewUrl,
            $"/projects/{ctx.ProjectId:N}/images/{image.Id:N}/content",
            size.Width,
            size.Height,
            ctx.CurrentToolCallId,
            SourceKind: "projectImage",
            SourceRefId: image.Id,
            ContentType: image.ContentType,
            FileName: image.FileName);
    }

    private static (int? Width, int? Height) ReadSize(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is null
            ? ((int?)null, (int?)null)
            : (bitmap.Width, bitmap.Height);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

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
        foreach (var canonSource in entity.CanonSources)
        {
            score += TextMatchScore(canonSource.SourceTitle, query, titleWeight: 12, detailWeight: 6);
            score += TextMatchScore(canonSource.Markdown, query, titleWeight: 12, detailWeight: 8);
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
            foreach (var canonSource in entity.CanonSources)
            {
                score += TextMatchScore(canonSource.SourceTitle, term, titleWeight: 18, detailWeight: 8);
                score += TextMatchScore(canonSource.Markdown, term, titleWeight: 18, detailWeight: 10);
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
        matchScore = score,
        summary = TruncatePropertyValue(entity.Summary),
        aliases = entity.Aliases.Take(8).ToArray(),
        wikiSections = CompactWikiSections(entity.WikiSections),
        canonSources = CompactCanonSources(entity.CanonSources),
        properties = CompactProperties(entity.Properties),
        visualCount = visuals?.Count ?? 0,
        visualExamples = (visuals ?? []).Select(example => new { example.Image.Id, example.Label, example.SortOrder, example.Image.AltText, example.Image.Prompt }),
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

    private static object SearchResultPayload(ProjectSearchResult result) => new
    {
        result.SourceType,
        result.SourceId,
        result.ContainerSourceId,
        result.Title,
        result.Snippet,
        result.Metadata,
        result.ChunkIndex,
        result.LexicalRank,
        result.LexicalPosition,
        result.VectorDistance,
        result.VectorPosition,
        result.Score,
        result.Reasons,
        content = Truncate(result.Content, 1_800),
    };

    private static object LinkPayload(EntityLink link) => new
    {
        edgeId = link.EdgeId,
        edgeType = link.EdgeType,
        direction = link.Direction.ToString(),
        otherEntityId = link.OtherEntityId,
        otherEntityName = link.OtherEntityName,
        otherEntityType = link.OtherEntityType,
        sortOrder = link.SortOrder,
        properties = link.Properties,
        summary = link.Summary,
        relationshipCitations = link.RelationshipCitations,
        isAutoLink = link.IsAutoLink,
    };

    private static object[] CompactCanonSources(IReadOnlyList<IngestCanonSource> sources) =>
        sources
            .Take(4)
            .Select(source => new
            {
                source.SourceTitle,
                source.SourceKind,
                markdown = TruncatePropertyValue(source.Markdown),
            })
            .ToArray();

    private static object[] CompactWikiSections(IReadOnlyList<IngestWikiSection> sections) =>
        sections
            .Take(4)
            .Select(section => new
            {
                section.Id,
                section.Title,
                body = TruncatePropertyValue(section.Body),
            })
            .ToArray();

    private static Dictionary<string, string?> CompactProperties(IReadOnlyDictionary<string, string?> properties)
    {
        var compact = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties
            .Where(property => !string.Equals(property.Key, "order", StringComparison.OrdinalIgnoreCase))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8))
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

    private sealed record ImageGenerationTargetResolution(
        PicturePageImageGenerationTarget? Target,
        string PromptAppendix,
        string? Error);

    private sealed record OrderedChapter(Chapter Chapter, int GlobalOrder);

    private sealed class ImpactCandidate(Chapter chapter, int globalOrder)
    {
        private readonly HashSet<string> _reasonKeys = new(StringComparer.OrdinalIgnoreCase);

        public Chapter Chapter { get; } = chapter;
        public int GlobalOrder { get; } = globalOrder;
        public int Score { get; private set; }
        public List<string> Reasons { get; } = [];
        public List<object> Evidence { get; } = [];

        public void Add(int score, string key, string reason)
        {
            Score += score;
            if (_reasonKeys.Add($"{key}:{reason}"))
                Reasons.Add(reason);
        }

        public void AddEvidence(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Evidence.Add(new { label, text });
        }
    }
}

public enum EditorChatToolMode
{
    Normal,
    ContestPreparation,
}

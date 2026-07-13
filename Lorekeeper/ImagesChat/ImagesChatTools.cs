using System.Text;
using System.Text.Json;
using Lorekeeper.ChapterVisuals;
using Lorekeeper.Chapters;
using Lorekeeper.Context;
using Lorekeeper.EditorChat;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Fonts;
using Lorekeeper.Images;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.ImagesChat;

public sealed class ImagesChatTools(
    IChapterService chapters,
    IProjectSearchService projectSearch,
    IProjectImageService projectImages,
    IEntityVisualExampleService entityVisualExamples,
    IEntityService entities,
    IProjectImageJobService imageJobs,
    IProjectImageGenerationRuntime imageRuntime,
    IChapterVisualService chapterVisuals,
    IProjectFontService projectFonts,
    IEditorContextService editorContext,
    IVisionModelClientFactory visionClient,
    IOptions<ProjectImageGenerationOptions> imageOptions,
    IOptions<EditorChatOptions> editorOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public Task<IList<AITool>> BuildAsync(ImagesChatToolContext context, CancellationToken cancellationToken = default)
    {
        IList<AITool> tools =
        [
            AIFunctionFactory.Create(
                method: (string? query = null, string[]? sourceTypes = null, int topK = 10) =>
                    ListSearchSourcesAsync(context, query, sourceTypes, topK),
                name: "list_search_sources",
                description: "Resolve searchable project source ids by title/name/type before a source-filtered search."),

            AIFunctionFactory.Create(
                method: (string sourceType, Guid sourceId, int? pageNumber = null) =>
                    ReadProjectSourceAsync(context, sourceType, sourceId, pageNumber),
                name: "read_project_source",
                description: "Read one paginated project source by sourceType and sourceId. Supports chapters, acts, entities, ingest sources, and chunks."),

            AIFunctionFactory.Create(
                method: (string query, int topK = 8, string[]? sourceTypes = null, string[]? sourceIds = null, Guid? containerSourceId = null, bool lexicalOnly = false) =>
                    SearchProjectAsync(context, query, topK, sourceTypes, sourceIds, containerSourceId, lexicalOnly),
                name: "search_project",
                description: "Hybrid keyword + semantic search over indexed project text. Use source filters when narrowing to chapters, context, acts, entities, or ingest sources."),

            AIFunctionFactory.Create(
                method: () => ListChaptersAsync(context),
                name: "list_chapters",
                description: "List chapters with ids, order, title, synopsis, body line count, and read_chapter page count."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, int? pageNumber = null) => ReadChapterAsync(context, chapterId, pageNumber),
                name: "read_chapter",
                description: "Read one paginated page of a chapter body with line numbers. Use pageNumber from returned pagination to continue."),

            AIFunctionFactory.Create(
                method: () => ListProjectImagesAsync(context),
                name: "list_project_images",
                description: "List project image library metadata, ids, preview URLs, source, prompts, model names, and sizes."),

            AIFunctionFactory.Create(
                method: (Guid imageId) => ReadProjectImageAsync(context, imageId),
                name: "read_project_image",
                description: "Read one project image's metadata and URLs. Use inspect_rendered_chapter_snapshots for visual layout inspection."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ReadEntityAsync(context, entityId),
                name: "read_entity",
                description: "Read a full entity and its ordered visual examples. Vision-ready providers receive the image bytes on the next round."),

            AIFunctionFactory.Create(
                method: (Guid entityId) => ListEntityVisualsAsync(context, entityId),
                name: "list_entity_visual_examples",
                description: "List and visually load the examples attached to an entity."),

            AIFunctionFactory.Create(
                method: (Guid entityId, Guid imageId, string? label = null) => AttachEntityVisualAsync(context, entityId, imageId, label),
                name: "attach_entity_visual_example",
                description: "Attach an existing project image to an eligible story entity as a labeled visual example."),

            AIFunctionFactory.Create(
                method: (Guid exampleId, string label, int? sortOrder = null) => UpdateEntityVisualAsync(context, exampleId, label, sortOrder),
                name: "update_entity_visual_example",
                description: "Relabel or reorder an entity visual example."),

            AIFunctionFactory.Create(
                method: (Guid exampleId) => DetachEntityVisualAsync(context, exampleId),
                name: "detach_entity_visual_example",
                description: "Detach an entity visual example without deleting the library image."),

            AIFunctionFactory.Create(
                method: (Guid chapterId) => ReadChapterVisualLayoutAsync(context, chapterId),
                name: "read_chapter_visual_layout",
                description: "Read a chapter visual mode and layout manifest, including placed image ids, text boxes, anchors, captions, and reading order."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, string question = "Describe the visual layout, image placement, text readability, and any issues.") =>
                    InspectRenderedChapterSnapshotsAsync(context, chapterId, question),
                name: "inspect_rendered_chapter_snapshots",
                description: "Render Picture Page or Illustrated Prose snapshots and inspect them with the active vision-capable chat provider."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, ProjectImageCropRegion crop, string? fileName = null, string? altText = null, EntityVisualTarget[]? entityTargets = null) =>
                    CropProjectImageAsync(context, sourceImageId, crop, fileName, altText, entityTargets),
                name: "crop_project_image",
                description: "Create a non-destructive project-library crop from an existing image using 0-100 percentage coordinates. Inspect the source first or use user-supplied coordinates, describe only the cropped subject in altText, and pass only explicit entityTargets. Source associations are never inherited."),

            AIFunctionFactory.Create(
                method: (Guid imageId, string label, ProjectImageMaskShape[] shapes) =>
                    CreateShapeMaskAsync(context, imageId, label, shapes),
                name: "create_shape_mask",
                description: "Create a PNG edit mask for an existing image from percentage-based rect/ellipse/polygon shapes. Transparent pixels are the editable regions."),

            AIFunctionFactory.Create(
                method: (string prompt, string? altText = null, string? size = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int count = 1, Guid[]? referenceImageIds = null, EntityVisualTarget[]? entityTargets = null, string? label = null, Guid? targetChapterId = null, Guid? targetPictureImageElementId = null) =>
                    GenerateImageAsync(context, prompt, altText, size, quality, outputFormat, outputCompression, count, referenceImageIds, entityTargets, label, targetChapterId, targetPictureImageElementId),
                name: "generate_image",
                description: "Generate library images. Ordinary scenes and prospective designs must omit entityTargets. Pass entityTargets only when the user explicitly approved or requested a purpose-built reference asset; otherwise attach an approved output later with attach_entity_visual_example. Use only grounded eligible entity ids and only still-approved referenceImageIds."),

            AIFunctionFactory.Create(
                method: (Guid sourceImageId, string prompt, Guid? maskId = null, ProjectImageMaskShape[]? maskShapes = null, string? maskLabel = null, string? altText = null, string? size = null, string? quality = null, string? outputFormat = null, int? outputCompression = null, int count = 1, Guid[]? referenceImageIds = null, EntityVisualTarget[]? entityTargets = null, bool inheritSourceEntityTargets = false, string? label = null) =>
                    EditImageAsync(context, sourceImageId, prompt, maskId, maskShapes, maskLabel, altText, size, quality, outputFormat, outputCompression, count, referenceImageIds, entityTargets, inheritSourceEntityTargets, label),
                name: "edit_image",
                description: "Edit a project image. Outputs do not inherit entity targets by default. Set inheritSourceEntityTargets=true only for a still-approved purpose-built reference whose identity role remains valid; ordinary scenes, redesigns, and prospective outputs stay unattached."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, Guid imageId, string? picturePagePlacementRole = null, Guid? targetPictureImageElementId = null) => AddProjectImageToChapterAsync(context, chapterId, imageId, picturePagePlacementRole, targetPictureImageElementId),
                name: "add_project_image_to_chapter",
                description: "Place an existing project image into a chapter visual layout. PicturePage roles are Freeform, Background, and ReplaceElement; ReplaceElement requires targetPictureImageElementId and preserves geometry/layer."),

            AIFunctionFactory.Create(
                method: (Guid chapterId, Guid imageId) => AddProjectImageToContextAsync(context, chapterId, imageId),
                name: "add_project_image_to_context",
                description: "Add an existing project image as explicit chapter context without changing the chapter layout."),
        ];

        return Task.FromResult(tools);
    }

    private async Task<string> ListSearchSourcesAsync(ImagesChatToolContext ctx, string? query, string[]? sourceTypes, int topK)
    {
        topK = Math.Clamp(topK, 1, 30);
        var sources = await projectSearch.ListSourcesAsync(ctx.ProjectId, query, sourceTypes, topK);
        return JsonSerializer.Serialize(sources, JsonOptions);
    }

    private async Task<string> ReadProjectSourceAsync(ImagesChatToolContext ctx, string sourceType, Guid sourceId, int? pageNumber)
    {
        var result = await projectSearch.ReadSourceAsync(ctx.ProjectId, sourceType, sourceId, pageNumber);
        if (result is null) return $"Error: source {sourceType}/{sourceId:N} was not found in this project.";
        if (string.Equals(sourceType, ProjectSearchSourceTypes.Entity, StringComparison.OrdinalIgnoreCase))
            await QueueEntityVisualsAsync(ctx, sourceId);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private async Task<string> ReadEntityAsync(ImagesChatToolContext ctx, Guid entityId)
    {
        var entity = await entities.GetAsync(ctx.ProjectId, entityId);
        if (entity is null) return $"Error: entity {entityId} not found in this project.";
        var visuals = await QueueEntityVisualsAsync(ctx, entityId);
        return JsonSerializer.Serialize(new { entity, visualExamples = visuals.Select(VisualPayload) }, JsonOptions);
    }

    private async Task<string> ListEntityVisualsAsync(ImagesChatToolContext ctx, Guid entityId) =>
        JsonSerializer.Serialize((await QueueEntityVisualsAsync(ctx, entityId)).Select(VisualPayload), JsonOptions);

    private async Task<string> AttachEntityVisualAsync(ImagesChatToolContext ctx, Guid entityId, Guid imageId, string? label)
    {
        try
        {
            var example = await entityVisualExamples.AttachAsync(ctx.ProjectId, entityId, imageId, label, EntityVisualExampleOrigin.Agent);
            ctx.AddModelOnlyImage(example.Image);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(VisualPayload(example), JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> UpdateEntityVisualAsync(ImagesChatToolContext ctx, Guid exampleId, string label, int? sortOrder)
    {
        try
        {
            var example = await entityVisualExamples.UpdateAsync(ctx.ProjectId, exampleId, label, sortOrder);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(VisualPayload(example), JsonOptions);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private async Task<string> DetachEntityVisualAsync(ImagesChatToolContext ctx, Guid exampleId)
    {
        await entityVisualExamples.DetachAsync(ctx.ProjectId, exampleId);
        ctx.MarkMutated();
        return JsonSerializer.Serialize(new { status = "detached", exampleId }, JsonOptions);
    }

    private async Task<IReadOnlyList<EntityVisualExampleView>> QueueEntityVisualsAsync(ImagesChatToolContext ctx, Guid entityId)
    {
        var visuals = await entityVisualExamples.ListForEntityAsync(ctx.ProjectId, entityId);
        foreach (var visual in visuals) ctx.AddModelOnlyImage(visual.Image);
        return visuals;
    }

    private async Task<string> SearchProjectAsync(
        ImagesChatToolContext ctx,
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
            : JsonSerializer.Serialize(results.Select(SearchResultPayload), JsonOptions);
    }

    private async Task<string> ListChaptersAsync(ImagesChatToolContext ctx)
    {
        var list = await chapters.ListAsync(ctx.ProjectId);
        if (list.Count == 0) return "No chapters in this project.";

        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var payload = list.Select(chapter => new
        {
            chapter.Id,
            order = chapter.Order + 1,
            chapter.Title,
            chapter.Synopsis,
            lines = ChapterFormatting.SplitLines(chapter.Body).Count,
            bodyChars = chapter.Body.Length,
            readChapterPages = CountTextPages(ChapterFormatting.WithLineNumbers(chapter.Body), pageMaxChars),
        });
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private async Task<string> ReadChapterAsync(ImagesChatToolContext ctx, Guid chapterId, int? pageNumber)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var text = ChapterFormatting.WithLineNumbers(chapter.Body);
        var pageMaxChars = EffectiveReadChapterPageMaxChars();
        var pageCount = CountTextPages(text, pageMaxChars);
        var requestedPage = Math.Clamp(pageNumber ?? 1, 1, pageCount);
        var start = text.Length == 0 ? 0 : (requestedPage - 1) * pageMaxChars;
        var content = text.Length == 0
            ? string.Empty
            : text.Substring(start, Math.Min(pageMaxChars, text.Length - start));

        return JsonSerializer.Serialize(new
        {
            chapter = new { chapter.Id, chapter.Title, chapter.Synopsis },
            pagination = new
            {
                currentPage = requestedPage,
                pageCount,
                pageMaxChars,
                totalTextChars = text.Length,
                hasPreviousPage = requestedPage > 1,
                previousPageNumber = requestedPage > 1 ? requestedPage - 1 : (int?)null,
                hasNextPage = requestedPage < pageCount,
                nextPageNumber = requestedPage < pageCount ? requestedPage + 1 : (int?)null,
            },
            previousPageArguments = requestedPage > 1 ? new { chapterId, pageNumber = requestedPage - 1 } : null,
            nextPageArguments = requestedPage < pageCount ? new { chapterId, pageNumber = requestedPage + 1 } : null,
            content,
        }, JsonOptions);
    }

    private async Task<string> ListProjectImagesAsync(ImagesChatToolContext ctx)
    {
        var images = await projectImages.ListAsync(ctx.ProjectId);
        if (images.Count == 0)
            return "No project images.";

        return JsonSerializer.Serialize(images.Select(ImagePayload), JsonOptions);
    }

    private async Task<string> ReadProjectImageAsync(ImagesChatToolContext ctx, Guid imageId)
    {
        var image = await projectImages.GetAsync(ctx.ProjectId, imageId);
        if (image is null)
            return $"Error: image {imageId:N} was not found in this project.";

        ctx.AddVisual(await BuildVisualAsync(
            ctx,
            image,
            title: image.FileName,
            caption: "Model-only image returned by read_project_image."));
        ctx.AddModelOnlyImage(image);

        return JsonSerializer.Serialize(new
        {
            image = ImagePayload(image),
            delivery = ctx.VisionReady
                ? "full image bytes will be supplied to the model on the next iteration"
                : "metadata only; the active chat provider is not vision-ready",
        }, JsonOptions);
    }

    private async Task<string> ReadChapterVisualLayoutAsync(ImagesChatToolContext ctx, Guid chapterId)
    {
        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var state = await chapterVisuals.GetAsync(chapterId);
        if (state is null)
            return $"Error: visual layout for chapter {chapterId} was not found.";

        var imageNames = (await projectImages.ListAsync(ctx.ProjectId))
            .ToDictionary(image => image.Id, image => image.FileName);
        var fontCatalog = await projectFonts.ListAsync(ctx.ProjectId, ctx.TurnCancellationToken);
        var fontNames = fontCatalog.ToDictionary(font => font.Key, font => font.Name, StringComparer.OrdinalIgnoreCase);
        var snapshots = await chapterVisuals.RenderSnapshotsAsync(chapterId);
        var textFit = snapshots.SelectMany(snapshot => snapshot.TextFitDiagnostics).ToList();
        var layoutDiagnostics = snapshots.SelectMany(snapshot => snapshot.LayoutDiagnostics).ToList();
        return JsonSerializer.Serialize(new
        {
            chapter = new { id = chapter.Id, chapter.Title, chapter.Synopsis },
            state.VisualMode,
            state.PageLayoutKind,
            state.IllustrationLayout,
            state.PageLayout,
            manifest = chapterVisuals.BuildManifest(state, imageNames, fontNames),
            fontCatalog = fontCatalog.Select(family => new
            {
                family.Key,
                family.Name,
                family.Category,
                family.IsBuiltIn,
                faces = family.Faces.Select(face => new { face.Weight, face.Italic, face.SubfamilyName }),
            }),
            textFit = new
            {
                allTextFits = state.VisualMode == ChapterVisualMode.PicturePage && snapshots.Count > 0
                    ? textFit.All(diagnostic => diagnostic.Fits)
                    : (bool?)null,
                elements = textFit,
            },
            layoutDiagnostics,
            allErrorsClear = layoutDiagnostics.All(diagnostic => diagnostic.Severity != "error"),
        }, JsonOptions);
    }

    private async Task<string> InspectRenderedChapterSnapshotsAsync(ImagesChatToolContext ctx, Guid chapterId, string question)
    {
        if (!ctx.VisionReady)
            return "Error: the active chat provider is not vision-ready. Run Test Vision in Settings > Providers.";

        var chapter = await chapters.GetAsync(chapterId);
        if (chapter is null || chapter.ProjectId != ctx.ProjectId)
            return $"Error: chapter {chapterId} not found in this project.";

        var snapshots = await chapterVisuals.RenderSnapshotsAsync(chapterId);
        if (snapshots.Count == 0)
            return "No visual snapshots rendered for this chapter.";

        var analyses = new List<object>();
        foreach (var snapshot in snapshots)
        {
            var visualId = Guid.NewGuid();
            var size = ReadSize(snapshot.Data);
            ctx.AddVisual(new ImagesChatVisualAttachment(
                visualId,
                $"Rendered page {snapshot.PageNumber}",
                $"Rendered snapshot inspected for chapter '{chapter.Title}'.",
                $"/projects/{ctx.ProjectId:N}/image-chat-visuals/{visualId:N}/content?maxEdge=640",
                $"/projects/{ctx.ProjectId:N}/image-chat-visuals/{visualId:N}/content",
                size.Width,
                size.Height,
                ctx.CurrentToolCallId,
                SourceKind: "renderedChapterSnapshot",
                SourceRefId: chapter.Id,
                ContentType: snapshot.ContentType,
                FileName: snapshot.FileName,
                Data: snapshot.Data));

            var prompt = $"""
                Inspect this rendered chapter page for Lorekeeper's Images tab.
                Chapter: {chapter.Title}
                User question: {question}

                Report only concrete visual observations: page composition, image placement, text readability, important visible content, and layout issues relevant to image generation or editing.
                """;
            var response = await visionClient.ReadImageAsync(
                ctx.ProviderId,
                snapshot.Data,
                snapshot.ContentType,
                prompt,
                maxOutputTokens: 1000);
            analyses.Add(new
            {
                snapshot.PageNumber,
                snapshot.FileName,
                analysis = response,
            });
        }

        return JsonSerializer.Serialize(new
        {
            chapter = new { chapter.Id, chapter.Title },
            analyses,
        }, JsonOptions);
    }

    private async Task<string> CreateShapeMaskAsync(ImagesChatToolContext ctx, Guid imageId, string label, ProjectImageMaskShape[] shapes)
    {
        if (shapes.Length == 0)
            return "Error: at least one mask shape is required.";

        var mask = await imageJobs.CreateMaskFromShapesAsync(
            ctx.ProjectId,
            imageId,
            new ProjectImageMaskShapeRequest(label, shapes),
            CancellationToken.None);
        ctx.MarkMutated();
        return JsonSerializer.Serialize(new
        {
            message = "Mask saved.",
            mask,
        }, JsonOptions);
    }

    private async Task<string> CropProjectImageAsync(
        ImagesChatToolContext ctx,
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
            var attached = new List<object>();
            foreach (var target in NormalizeTargets(entityTargets))
            {
                var example = await entityVisualExamples.AttachAsync(
                    ctx.ProjectId,
                    target.EntityId,
                    image.Id,
                    target.Label,
                    EntityVisualExampleOrigin.Agent);
                attached.Add(VisualPayload(example));
            }

            ctx.AddVisual(await BuildVisualAsync(ctx, image, image.FileName, "Cropped project image saved to the library."));
            ctx.AddModelOnlyImage(image);
            ctx.MarkMutated();
            return JsonSerializer.Serialize(new
            {
                status = "cropped",
                sourceImageId,
                image = ImagePayload(image),
                attached,
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> GenerateImageAsync(
        ImagesChatToolContext ctx,
        string prompt,
        string? altText,
        string? size,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int count,
        Guid[]? referenceImageIds,
        EntityVisualTarget[]? entityTargets,
        string? label,
        Guid? targetChapterId,
        Guid? targetPictureImageElementId)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: prompt is required.";

        var targetResolution = await ResolvePicturePageGenerationTargetAsync(ctx, targetChapterId, targetPictureImageElementId);
        if (targetResolution.Error is not null)
            return targetResolution.Error;

        var promptText = targetResolution.PromptAppendix is { Length: > 0 } appendix
            ? prompt.Trim() + appendix
            : prompt.Trim();
        var effectiveSize = string.IsNullOrWhiteSpace(size) && targetResolution.Target is { } target
            ? target.RecommendedSize
            : CleanOr(size, imageOptions.Value.DefaultSize);

        var targetValidation = await entityVisualExamples.ValidateTargetsAsync(ctx.ProjectId, entityTargets);
        if (!targetValidation.IsValid)
            return $"Error: {targetValidation.Error} Use an entity id returned by project/entity reads; otherwise omit entityTargets.";

        ProjectImageJobView job;
        try
        {
            job = await imageJobs.CreateGenerateJobAsync(ctx.ProjectId, new ProjectImageGenerateJobRequest(
                promptText,
                effectiveSize,
                CleanOr(quality, imageOptions.Value.DefaultQuality),
                CleanOr(outputFormat, imageOptions.Value.DefaultOutputFormat),
                outputCompression,
                altText?.Trim() ?? string.Empty,
                Math.Clamp(count, 1, Math.Max(1, imageOptions.Value.MaxOutputs)),
                (referenceImageIds ?? []).Distinct().ToList(),
                label,
                targetValidation.Targets), ctx.TurnCancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
        return await RunQueuedJobToolAsync(ctx, job.Id);
    }

    private async Task<ImageGenerationTargetResolution> ResolvePicturePageGenerationTargetAsync(
        ImagesChatToolContext ctx,
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

    private async Task<string> EditImageAsync(
        ImagesChatToolContext ctx,
        Guid sourceImageId,
        string prompt,
        Guid? maskId,
        ProjectImageMaskShape[]? maskShapes,
        string? maskLabel,
        string? altText,
        string? size,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        int count,
        Guid[]? referenceImageIds,
        EntityVisualTarget[]? entityTargets,
        bool inheritSourceEntityTargets,
        string? label)
    {
        if (sourceImageId == Guid.Empty)
            return "Error: sourceImageId is required.";
        if (string.IsNullOrWhiteSpace(prompt))
            return "Error: prompt is required.";

        var targetValidation = await entityVisualExamples.ValidateTargetsAsync(ctx.ProjectId, entityTargets);
        if (!targetValidation.IsValid)
            return $"Error: {targetValidation.Error} Use an entity id returned by project/entity reads; otherwise omit entityTargets.";

        Guid? effectiveMaskId = maskId;
        if (effectiveMaskId is null && maskShapes is { Length: > 0 })
        {
            var mask = await imageJobs.CreateMaskFromShapesAsync(
                ctx.ProjectId,
                sourceImageId,
                new ProjectImageMaskShapeRequest(maskLabel ?? "Agent edit mask", maskShapes),
                ctx.TurnCancellationToken);
            effectiveMaskId = mask.Id;
        }

        ProjectImageJobView job;
        try
        {
            job = await imageJobs.CreateEditJobAsync(ctx.ProjectId, new ProjectImageEditJobRequest(
                sourceImageId,
                prompt.Trim(),
                CleanOr(size, imageOptions.Value.DefaultSize),
                CleanOr(quality, imageOptions.Value.DefaultQuality),
                CleanOr(outputFormat, imageOptions.Value.DefaultOutputFormat),
                outputCompression,
                altText?.Trim() ?? string.Empty,
                Math.Clamp(count, 1, Math.Max(1, imageOptions.Value.MaxOutputs)),
                MaskPngDataUrl: null,
                ReferenceImageIds: (referenceImageIds ?? []).Distinct().ToList(),
                Label: label,
                ExistingMaskId: effectiveMaskId,
                EntityTargets: targetValidation.Targets,
                InheritSourceEntityTargets: inheritSourceEntityTargets), ctx.TurnCancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: {ex.Message}";
        }
        return await RunQueuedJobToolAsync(ctx, job.Id);
    }

    private async Task<string> RunQueuedJobToolAsync(ImagesChatToolContext ctx, Guid jobId)
    {
        ctx.TrackImageGenerationJob(jobId);
        try
        {
            await imageRuntime.EnqueueProjectAsync(ctx.ProjectId, ctx.TurnCancellationToken);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(imageOptions.Value.AgentJobWaitTimeoutSeconds, 1, 3600));
            var completed = await imageRuntime.WaitForJobCompletionAsync(jobId, timeout, ctx.TurnCancellationToken);
            var job = await imageJobs.GetJobAsync(ctx.ProjectId, jobId, ctx.TurnCancellationToken);
            if (job is null)
                return $"Error: image job {jobId:N} was not found after queueing.";

            var outputs = new List<object>();
            foreach (var imageId in job.OutputImageIds)
            {
                var image = await projectImages.GetAsync(ctx.ProjectId, imageId, ctx.TurnCancellationToken);
                if (image is null)
                    continue;

                var caption = string.Equals(ctx.CurrentToolName, "edit_image", StringComparison.Ordinal)
                    ? "Edited output saved to the image library."
                    : "Generated output saved to the image library.";
                ctx.AddVisual(await BuildVisualAsync(ctx, image, title: image.FileName, caption: caption));
                ctx.AddModelOnlyImage(image);
                outputs.Add(ImagePayload(image));
            }

            ctx.MarkMutated();
            return JsonSerializer.Serialize(new
            {
                completed,
                job = JobPayload(job),
                images = outputs,
                note = completed ? null : "Timed out waiting for the image job. The Images tab will continue showing progress.",
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ctx.TurnCancellationToken.IsCancellationRequested)
        {
            await imageRuntime.CancelJobAsync(ctx.ProjectId, jobId, CancellationToken.None);
            return "Cancelled.";
        }
    }

    private async Task<string> AddProjectImageToChapterAsync(
        ImagesChatToolContext ctx,
        Guid chapterId,
        Guid imageId,
        string? picturePagePlacementRole,
        Guid? targetPictureImageElementId)
    {
        if (!TryParseOptionalEnum(picturePagePlacementRole, out PicturePageImagePlacementRole? parsedRole, out var roleError))
            return roleError!;
        if (targetPictureImageElementId is { } targetId && targetId != Guid.Empty && parsedRole != PicturePageImagePlacementRole.ReplaceElement)
            return "Error: targetPictureImageElementId requires picturePagePlacementRole=ReplaceElement.";
        if (parsedRole == PicturePageImagePlacementRole.ReplaceElement
            && (targetPictureImageElementId is null || targetPictureImageElementId == Guid.Empty))
            return "Error: picturePagePlacementRole=ReplaceElement requires targetPictureImageElementId.";
        var result = await chapterVisuals.AddImageToChapterAsync(
            ctx.ProjectId,
            chapterId,
            imageId,
            new ChapterImagePlacementRequest(parsedRole ?? PicturePageImagePlacementRole.Freeform, targetPictureImageElementId));
        ctx.MarkMutated();
        return JsonSerializer.Serialize(new
        {
            message = "Image placed in chapter.",
            chapterId,
            imageId,
            elementId = result.ElementId,
            result.State.VisualMode,
        }, JsonOptions);
    }

    private async Task<string> AddProjectImageToContextAsync(ImagesChatToolContext ctx, Guid chapterId, Guid imageId)
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
        ctx.MarkMutated();
        return "Image added to chapter context.";
    }

    private async Task<ImagesChatVisualAttachment> BuildVisualAsync(
        ImagesChatToolContext ctx,
        ProjectImageView image,
        string? title = null,
        string? caption = null)
    {
        var data = await projectImages.GetDataAsync(ctx.ProjectId, image.Id, maxEdge: null, CancellationToken.None);
        var size = data is null ? (Width: (int?)null, Height: (int?)null) : ReadSize(data.Data);
        return new ImagesChatVisualAttachment(
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

    private int EffectiveReadChapterPageMaxChars() => Math.Max(256, editorOptions.Value.ReadChapterPageMaxChars);

    private static int CountTextPages(string text, int pageMaxChars) =>
        string.IsNullOrEmpty(text) ? 1 : (text.Length + pageMaxChars - 1) / pageMaxChars;

    private static string CleanOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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

    private static IReadOnlyList<EntityVisualTarget> NormalizeTargets(IEnumerable<EntityVisualTarget>? targets) =>
        (targets ?? []).Where(target => target.EntityId != Guid.Empty)
            .Select(target => new EntityVisualTarget(target.EntityId, target.Label?.Trim() ?? string.Empty))
            .DistinctBy(target => target.EntityId)
            .ToList();

    private static object VisualPayload(EntityVisualExampleView example) => new
    {
        example.Id, example.EntityId, example.EntityName, example.EntityType, example.Label, example.SortOrder,
        image = ImagePayload(example.Image),
    };

    private static object ImagePayload(ProjectImageView image) => new
    {
        image.Id,
        image.FileName,
        image.ContentType,
        image.PreviewUrl,
        FullUrl = image.PreviewUrl.Replace("?maxEdge=640", string.Empty, StringComparison.Ordinal),
        image.AltText,
        image.Source,
        image.Prompt,
        image.GenerationModel,
        image.SourceMetadataJson,
        image.CreatedAt,
        image.UpdatedAt,
        image.SizeBytes,
    };

    private static object JobPayload(ProjectImageJobView job) => new
    {
        job.Id,
        job.Kind,
        job.Status,
        job.Label,
        job.Prompt,
        job.Size,
        job.Quality,
        job.OutputFormat,
        job.Count,
        job.SourceImageId,
        job.MaskId,
        job.ReferenceImageIds,
        job.OutputImageIds,
        job.OutputStates,
        job.OutputErrors,
        job.Error,
        job.CreatedAt,
        job.StartedAt,
        job.CompletedAt,
    };

    private static (int? Width, int? Height) ReadSize(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        return bitmap is null
            ? ((int?)null, (int?)null)
            : (bitmap.Width, bitmap.Height);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

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

    private sealed record ImageGenerationTargetResolution(
        PicturePageImageGenerationTarget? Target,
        string PromptAppendix,
        string? Error);
}

using System.Text.Json;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Composition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Lorekeeper.Publish;

public sealed record PublishAssistantContext(
    Guid ProjectId,
    Guid ConversationId = default,
    CancellationToken TurnCancellationToken = default);

public interface IPublishAssistantTools
{
    Task<IList<AITool>> BuildAsync(
        PublishAssistantContext context,
        CancellationToken cancellationToken = default);
}

public sealed class PublishAssistantTools(
    IPublicationEditionService editions,
    IPublishService publishing,
    IPublicationEditionMigrationService migrations,
    IPublicationRenderService renders,
    IPublicationCoverService covers,
    IPublicationPackageService packages,
    IManuscriptStyleService manuscriptStyles,
    IProjectImageService projectImages,
    IDatabaseMigrationRecoveryService recovery,
    ICompositionService? compositions = null,
    AppDbContext? db = null,
    IImagePromptComposer? imagePrompts = null,
    IProjectImageJobService? imageJobs = null,
    IProjectImageGenerationRuntime? imageRuntime = null) : IPublishAssistantTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public Task<IList<AITool>> BuildAsync(
        PublishAssistantContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IList<AITool> tools =
        [
            AIFunctionFactory.Create(
                method: () => ReadEditionsAsync(context),
                name: "list_publication_editions",
                description: "List every publication edition with stable IDs, status, default state, and revision tokens."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ReadWorkspaceAsync(context, editionId),
                name: "read_publication_edition",
                description: "Read one compact edition workspace: complete edition settings plus section, matter, style, placement, visual, and diagnostic counts. Detailed content is available through focused paginated tools."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 30) => ListNamedStylesAsync(context, offset, limit),
                name: "list_publication_named_styles",
                description: "List a compact page of project named styles with stable IDs, definitions, revisions, and continuation metadata."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 30) => ListProjectImagesAsync(context, offset, limit),
                name: "list_publication_project_images",
                description: "List a compact page of project images with stable asset IDs, file metadata, alt text, source, preview URL, and continuation metadata."),
            AIFunctionFactory.Create(
                method: (int offset = 0, int limit = 40) => ListManuscriptVisualsAsync(context, offset, limit),
                name: "list_publication_manuscript_visuals",
                description: "List a compact page of manuscript Figures and Designed Pages with stable target IDs, accessibility state, and composition IDs. No image bytes or manuscript text are returned."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid variantId, int semanticStart = 0, int semanticCount = 20, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadPageCompositionAsync(context, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount),
                name: "read_publication_page_composition",
                description: "Read one selected geometry variant losslessly in bounded object pages, including complete surface, layers, styles, guides, object fields, semantic excerpts, and revisions."),
            AIFunctionFactory.Create(
                method: (Guid editionId, string targetKind, Guid targetId, Guid? variantId = null) => ReadLayoutGenerationTargetAsync(context, editionId, targetKind, targetId, variantId),
                name: "read_publication_generation_target",
                description: "Resolve exact server-owned geometry for a Figure, page surface/frame, or cover surface/frame. Page targets require the exact selected composition variantId. Use this before layout-bound image generation; never invent dimensions."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid variantId) => ValidateCompositionAsync(context, editionId, variantId),
                name: "validate_publication_page_composition",
                description: "Validate one Designed Page variant for geometry, semantic coverage, reading order, accessibility, overflow, image DPI, font readiness, and edition compatibility. Returns compact prioritized diagnostics."),
            AIFunctionFactory.Create(
                method: (Guid editionId, string targetKind, Guid targetId, ImageGenerationBrief brief, Guid? variantId = null, ImageReferenceUse[]? references = null, string? altText = null) =>
                    QueueLayoutBoundImageAsync(context, editionId, targetKind, targetId, variantId, brief, references, altText),
                name: "generate_publication_layout_image",
                description: "Queue one image-library generation for an exact Figure, page frame/surface, or cover frame/surface. Page targets require the exact selected composition variantId. Lorekeeper derives canvas size, aspect ratio, safe regions, bleed, gutter, spine, barcode, and text reserves; this tool accepts no competing dimensions. Returns only compact job and target metadata and never places the output automatically."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, Guid editionId) => GetOrCreateCompositionVariantAsync(context, compositionId, editionId),
                name: "get_or_create_publication_composition_variant",
                description: "Get the exact geometry-keyed variant for a Designed Page and edition, creating an independent default only when absent."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCompositionElementAsync(context, editionId, variantId, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_composition_element",
                description: "Revision-check patch one stable composition object, guide, layer, or style using only changed fields. Preserve unrelated state and reserve full-scene staging for structural edits."),
            AIFunctionFactory.Create(
                method: (Guid variantId, long expectedRevision, CompositionScene scene) => StageCompositionAsync(context, variantId, expectedRevision, scene),
                name: "stage_publication_composition",
                description: "Submit a complete large scene once. Returns an opaque one-use stage ID, compact summary, and diagnostics without echoing the scene."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid stageId, long expectedRevision) => ApplyCompositionStageAsync(context, editionId, stageId, expectedRevision),
                name: "apply_publication_composition_stage",
                description: "Apply an already staged scene using only its one-use stage ID and expected revision. Never repeat the scene payload."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations) => StageCompositionSemanticAsync(context, compositionId, expectedRevision, operations),
                name: "stage_publication_composition_semantic",
                description: "Stage focused block or inline-mark operations against a Designed Page's sole semantic manuscript. Returns a compact one-use stage ID without repeating content."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid stageId, long expectedRevision) => ApplyCompositionSemanticStageAsync(context, editionId, stageId, expectedRevision),
                name: "apply_publication_composition_semantic_stage",
                description: "Apply a staged Designed Page semantic edit by one-use stage ID and exact composition revision."),
            AIFunctionFactory.Create(
                method: (Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene) => StageCompositionWorkspaceAsync(context, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene),
                name: "stage_publication_composition_workspace",
                description: "Atomically stage coupled Designed Page content and scene changes. Semantic fragments contain paragraph, heading, sceneBreak, blockQuote, or listItem blocks only; scene images carry visual content. Submit the complete payload once."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid stageId, long expectedCompositionRevision) => ApplyCompositionWorkspaceStageAsync(context, editionId, stageId, expectedCompositionRevision),
                name: "apply_publication_composition_workspace_stage",
                description: "Apply a coupled content-and-scene stage by one-use stage ID; both stored revisions are checked without repeating the payload."),
            AIFunctionFactory.Create(
                method: (string name, PublicationEditionFormat format, PublicationVendor vendor) =>
                    CreateEditionAsync(context, name, format, vendor),
                name: "create_publication_edition",
                description: "Create a publication edition and return its first revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, string name, long expectedRevision) =>
                    CloneEditionAsync(context, editionId, name, expectedRevision),
                name: "clone_publication_edition",
                description: "Clone an edition at an expected revision. The clone receives a new identity and its ISBN is cleared."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionUpdate update) =>
                    UpdateEditionAsync(context, editionId, update),
                name: "update_publication_edition",
                description: "Revision-check metadata, geometry, typography, content, and production settings through the same service as the UI. Edition format is immutable; create a new edition for Paperback, EPUB, or Digital PDF instead of trying to change format."),
            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision) =>
                    SetDefaultAsync(context, editionId, expectedRevision),
                name: "set_default_publication_edition",
                description: "Make an active edition the project default using its expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision) =>
                    ArchiveAsync(context, editionId, expectedRevision),
                name: "archive_publication_edition",
                description: "Archive an edition using its expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionOutlineItemUpdate[] updates, long expectedRevision) =>
                    SetContentAsync(context, editionId, updates, expectedRevision),
                name: "set_publication_content",
                description: "Include or exclude acts and chapters for one edition through stable IDs and an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionOutlineItemOrder[] orderedItems, long expectedRevision) =>
                    ReorderContentAsync(context, editionId, orderedItems, expectedRevision),
                name: "reorder_publication_content",
                description: "Set the complete edition-specific reading order of acts and chapters using stable IDs and an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationMatterInput input, long expectedRevision) =>
                    UpsertMatterAsync(context, editionId, input, expectedRevision),
                name: "upsert_publication_matter",
                description: "Create or update semantic front/back matter with its manuscript JSON and revision tokens."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid matterId, long expectedRevision) =>
                    DeleteMatterAsync(context, editionId, matterId, expectedRevision),
                name: "delete_publication_matter",
                description: "Delete one matter item from an edition at an expected edition revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationEditionStyleMappingInput input, long expectedRevision) =>
                    UpsertStyleMappingAsync(context, editionId, input, expectedRevision),
                name: "upsert_publication_style_mapping",
                description: "Map a named manuscript style to edition-specific properties through the owning service."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid mappingId, long expectedRevision) =>
                    DeleteStyleMappingAsync(context, editionId, mappingId, expectedRevision),
                name: "delete_publication_style_mapping",
                description: "Delete an edition style mapping at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationImagePlacementCreate input, long expectedRevision) =>
                    AddPlacementAsync(context, editionId, input, expectedRevision),
                name: "add_publication_image_placement",
                description: "Place a project image at an included edition target."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid placementId, PublicationImagePlacementUpdate input, long expectedRevision) =>
                    UpdatePlacementAsync(context, editionId, placementId, input, expectedRevision),
                name: "update_publication_image_placement",
                description: "Update an edition image placement at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid[] orderedPlacementIds, long expectedRevision) =>
                    ReorderPlacementsAsync(context, editionId, orderedPlacementIds, expectedRevision),
                name: "reorder_publication_image_placements",
                description: "Reorder all placements in one target/position group."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid placementId, long expectedRevision) =>
                    DeletePlacementAsync(context, editionId, placementId, expectedRevision),
                name: "delete_publication_image_placement",
                description: "Delete an edition image placement at an expected revision."),
            AIFunctionFactory.Create(
                method: (Guid leftEditionId, Guid rightEditionId) =>
                    CompareAsync(context, leftEditionId, rightEditionId),
                name: "compare_publication_editions",
                description: "Compare two editions and return material product/content differences."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ReadAuditAsync(context, editionId),
                name: "read_publication_audit",
                description: "Read immutable edition mutation history with before/after fingerprints."),
            AIFunctionFactory.Create(
                method: () => ReadMigrationAsync(),
                name: "read_publication_migration_state",
                description: "Read the publication-edition migration journal and protected backup diagnostics. Restore remains user-confirmed."),
            AIFunctionFactory.Create(
                method: (PublicationEditionFormat format, PublicationVendor vendor) =>
                    ReadPressRuntimeReadiness(format, vendor),
                name: "read_publication_pdf_runtime",
                description: "Read Lorekeeper Press readiness, protocol version, renderer version, supported profiles, limits, and capabilities before proposing or requesting PDF generation."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => RequestRenderAsync(context, editionId),
                name: "request_publication_render",
                description: "Queue deterministic interior and cover PDF rendering for a paperback edition."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ListRendersAsync(context, editionId),
                name: "list_publication_renders",
                description: "Inspect render status, progress, diagnostics, immutable artifacts, hashes, and stale state."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ListDownloadsAsync(context, editionId),
                name: "list_publication_downloads",
                description: "List generated publication artifacts with freshness, hashes, and safe in-app view/download URLs. Never claim a file was downloaded; the user must open a returned URL."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid jobId) => CancelRenderAsync(context, editionId, jobId),
                name: "cancel_publication_render",
                description: "Cancel a queued or running press render."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid jobId) => ReadPageMapAsync(context, editionId, jobId),
                name: "read_publication_page_map",
                description: "Read stable manuscript block IDs mapped to pages in one completed render."),
            AIFunctionFactory.Create(
                method: (Guid editionId, Guid leftJobId, Guid rightJobId) =>
                    CompareRendersAsync(context, editionId, leftJobId, rightJobId),
                name: "compare_publication_renders",
                description: "Explain page-count and block-pagination changes between two renders."),
            AIFunctionFactory.Create(
                method: (Guid editionId, int objectStart = 0, int objectCount = 30, int structureStart = 0, int structureCount = 30) => ReadCoverAsync(context, editionId, objectStart, objectCount, structureStart, structureCount),
                name: "read_publication_cover_design",
                description: "Read compact cover copy, calculated geometry, diagnostics, scene layers, and one bounded page of scene objects. Use continuation metadata for additional objects."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => ValidateCoverAsync(context, editionId),
                name: "validate_publication_cover_composition",
                description: "Validate the current cover for geometry, safe regions, folds, barcode reserve, accessibility, reading order, images, and current spine state. Returns compact prioritized diagnostics."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublicationCoverDesignUpdate update) =>
                    UpdateCoverAsync(context, editionId, update),
                name: "update_publication_cover_design",
                description: "Update cover copy, colors, barcode behavior, and template acknowledgement. Use staged cover-composition tools for scene objects."),
            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) => PatchCoverElementAsync(context, editionId, expectedRevision, targetKind, targetId, patch),
                name: "patch_publication_cover_element",
                description: "Revision-check patch one stable cover object, guide, layer, or style using only changed fields. Preserve unrelated cover state; use full-scene staging for structural changes."),
            AIFunctionFactory.Create(
                method: (Guid editionId, long expectedRevision, CompositionScene scene) =>
                    StageCoverCompositionAsync(context, editionId, expectedRevision, scene),
                name: "stage_publication_cover_composition",
                description: "Submit a complete cover scene exactly once. Returns an opaque one-use stage ID and compact diagnostics without echoing the scene."),
            AIFunctionFactory.Create(
                method: (Guid stageId, long expectedRevision) =>
                    ApplyCoverCompositionStageAsync(context, stageId, expectedRevision),
                name: "apply_publication_cover_composition_stage",
                description: "Apply a staged cover scene using only its one-use stage ID and expected cover revision. Never repeat the scene payload."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => PreflightAsync(context, editionId),
                name: "preflight_publication_edition",
                description: "Run the same versioned metadata, content, PDF, cover, barcode, staleness, and scope checks as the UI."),
            AIFunctionFactory.Create(
                method: (Guid editionId, PublishExportFormat format) =>
                    ExportAsync(context, editionId, format),
                name: "export_publication_edition",
                description: "Prepare a TXT, Markdown, or EPUB download and return safe metadata, the bound source fingerprint, and its in-app regeneration URL."),
            AIFunctionFactory.Create(
                method: (Guid editionId) => BuildPackageAsync(context, editionId),
                name: "build_publication_package",
                description: "Build the selected product-form package (EPUB or Lorekeeper-validated paperback PDFs), manifest, report, and cover image when preflight has no errors."),
        ];
        return Task.FromResult(tools);
    }

    private async Task<string> ReadEditionsAsync(PublishAssistantContext context) =>
        Serialize(await editions.ListAsync(context.ProjectId));

    private async Task<string> ReadWorkspaceAsync(PublishAssistantContext context, Guid editionId)
    {
        var workspace = await publishing.GetWorkspaceAsync(context.ProjectId, editionId);
        return Serialize(new
        {
            ok = true,
            targetId = workspace.Edition.Id,
            revision = workspace.Edition.Revision,
            summary = $"{workspace.Sections.Sum(section => section.Chapters.Count)} chapter(s), {workspace.Matter.Count} matter item(s), {workspace.Placements.Count} edition illustration(s), and {workspace.StyleMappings.Count} style mapping(s).",
            edition = workspace.Edition,
            sections = workspace.Sections.Select(section => new
            {
                section.ActId,
                section.Title,
                section.IsUnassigned,
                section.IsIncluded,
                chapters = section.Chapters.Select(chapter => new
                {
                    chapter.Id,
                    chapter.Title,
                    chapter.IsIncluded,
                    chapter.FigureCount,
                    chapter.DesignedPageCount,
                    chapter.LayoutDiagnosticCount,
                }),
            }),
            counts = new
            {
                matter = workspace.Matter.Count,
                styleMappings = workspace.StyleMappings.Count,
                placements = workspace.Placements.Count,
                availableEditions = workspace.Editions.Count,
            },
            workspace.SourceFingerprint,
        });
    }

    private async Task<string> QueueLayoutBoundImageAsync(
        PublishAssistantContext context,
        Guid editionId,
        string targetKind,
        Guid targetId,
        Guid? variantId,
        ImageGenerationBrief brief,
        ImageReferenceUse[]? references,
        string? altText)
    {
        if (imagePrompts is null || imageJobs is null || imageRuntime is null)
            return Serialize(new { ok = false, code = "IMAGE_RUNTIME_UNAVAILABLE", targetId, summary = "Image generation is unavailable." });
        try
        {
            var compiled = await imagePrompts.CompileGenerationAsync(
                context.ProjectId,
                brief,
                references,
                new ImageGenerationTarget { EditionId = editionId, TargetKind = targetKind, TargetId = targetId, VariantId = variantId },
                context.TurnCancellationToken);
            var job = await imageJobs.CreateGenerateJobAsync(
                context.ProjectId,
                new ProjectImageGenerateJobRequest(
                    compiled.Prompt,
                    compiled.Size,
                    "auto",
                    "png",
                    null,
                    altText?.Trim() ?? string.Empty,
                    1,
                    compiled.ReferenceImageIds,
                    Label: "Publish layout image",
                    BriefJson: compiled.BriefJson,
                    ReferenceManifestJson: compiled.ReferenceManifestJson,
                    TargetGeometryJson: compiled.TargetGeometryJson),
                context.TurnCancellationToken);
            await imageRuntime.EnqueueProjectAsync(context.ProjectId, context.TurnCancellationToken);
            return Serialize(new
            {
                ok = true,
                targetId,
                jobId = job.Id,
                status = job.Status,
                requestedCanvas = compiled.Size,
                referenceCount = compiled.ReferenceImageIds.Count,
                summary = "Generation queued in the project image library; inspect the result before placing it.",
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Serialize(new { ok = false, code = "GENERATION_REJECTED", targetId, summary = ex.Message });
        }
    }

    private string ReadPressRuntimeReadiness(PublicationEditionFormat format, PublicationVendor vendor)
    {
        var readiness = renders.GetRuntimeReadiness(format, vendor);
        PublicationPressDescription? description = readiness.IsReady
            ? renders.GetRuntimeDescription()
            : null;
        return Serialize(new { readiness, description });
    }

    private async Task<string> ListNamedStylesAsync(PublishAssistantContext context, int offset, int limit)
    {
        var all = await manuscriptStyles.ListAsync(context.ProjectId);
        var start = Math.Clamp(offset, 0, all.Count);
        var take = Math.Clamp(limit, 1, 60);
        var items = all.Skip(start).Take(take).ToList();
        return Serialize(new { ok = true, summary = $"{all.Count} named style(s).", items, continuation = Continuation(start, items.Count, all.Count) });
    }

    private async Task<string> ListProjectImagesAsync(PublishAssistantContext context, int offset, int limit)
    {
        var all = await projectImages.ListAsync(context.ProjectId);
        var start = Math.Clamp(offset, 0, all.Count);
        var take = Math.Clamp(limit, 1, 60);
        var items = all.Skip(start).Take(take).Select(image => new
        {
            image.Id,
            image.FileName,
            image.ContentType,
            image.PreviewUrl,
            image.AltText,
            image.Source,
            image.SizeBytes,
            image.UpdatedAt,
        }).ToList();
        return Serialize(new { ok = true, summary = $"{all.Count} project image(s).", items, continuation = Continuation(start, items.Count, all.Count) });
    }

    private async Task<string> ListManuscriptVisualsAsync(PublishAssistantContext context, int offset, int limit)
    {
        var start = Math.Max(0, offset);
        var take = Math.Clamp(limit, 1, 80);
        var store = db ?? throw new InvalidOperationException("Publication visual storage is unavailable.");
        var chapters = await store.Chapters.AsNoTracking().Where(item => item.ProjectId == context.ProjectId)
            .OrderBy(item => item.Order).Select(item => new { item.Id, item.Title, item.ManuscriptJson }).ToListAsync(context.TurnCancellationToken);
        var visuals = chapters.SelectMany(chapter => ManuscriptCodec.Deserialize(chapter.ManuscriptJson).Content
            .Where(block => block.Type is ManuscriptBlockType.Figure or ManuscriptBlockType.DesignedPage)
            .Select(block => new
            {
                chapterId = chapter.Id, chapter.Title, blockId = block.Id, type = block.Type.ToString(),
                block.ImageId, compositionId = block.PageCompositionId,
                accessibility = block.Type == ManuscriptBlockType.Figure
                    ? block.Decorative ? "decorative" : string.IsNullOrWhiteSpace(block.AltText) ? "missing-alt" : "described"
                    : "composition-reading-order",
            })).ToList();
        var items = visuals.Skip(start).Take(take).ToList();
        return Serialize(new { ok = true, summary = $"{visuals.Count} manuscript visual(s).", items, nextOffset = start + items.Count < visuals.Count ? start + items.Count : (int?)null });
    }

    private async Task<string> ReadPageCompositionAsync(PublishAssistantContext context, Guid compositionId, Guid variantId, int semanticStart, int semanticCount, int objectStart, int objectCount, int structureStart, int structureCount)
    {
        var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.");
        try { return await CompositionAgentPayloads.ReadVariantAsync(service, context.ProjectId, compositionId, variantId, semanticStart, semanticCount, objectStart, objectCount, structureStart, structureCount, context.TurnCancellationToken); }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException) { return Serialize(new { ok = false, code = "NOT_FOUND", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> PatchCompositionElementAsync(PublishAssistantContext context, Guid editionId, Guid variantId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch) =>
        await CompositionAgentPayloads.PatchElementAsync(
            compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."),
            context.ProjectId,
            variantId,
            expectedRevision,
            targetKind,
            targetId,
            patch,
            context.TurnCancellationToken);

    private async Task<string> ReadLayoutGenerationTargetAsync(PublishAssistantContext context, Guid editionId, string targetKind, Guid targetId, Guid? variantId)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); return Serialize(new { ok = true, descriptor = await service.DescribeGenerationTargetAsync(context.ProjectId, editionId, targetKind, targetId, variantId, context.TurnCancellationToken) }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_TARGET", targetId, summary = ex.Message }); }
    }

    private async Task<string> ValidateCompositionAsync(PublishAssistantContext context, Guid editionId, Guid variantId)
    {
        try
        {
            var result = await (compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable.")).ValidateVariantAsync(context.ProjectId, editionId, variantId, context.TurnCancellationToken);
            return Serialize(new { ok = result.ErrorCount == 0, targetId = result.TargetId, revision = result.Revision, summary = $"Validation found {result.ErrorCount} error(s) and {result.WarningCount} warning(s).", diagnosticCounts = new { errors = result.ErrorCount, warnings = result.WarningCount }, diagnostics = result.Diagnostics });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> GetOrCreateCompositionVariantAsync(PublishAssistantContext context, Guid compositionId, Guid editionId)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var variant = await service.GetOrCreateVariantAsync(context.ProjectId, compositionId, editionId, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = variant.Id, revision = variant.Revision, summary = "Exact geometry variant is ready.", mutation = new { kind = "pageComposition", id = compositionId, selectId = variant.Id } }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_TARGET", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionAsync(PublishAssistantContext context, Guid variantId, long expectedRevision, CompositionScene scene)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var stage = await service.StageVariantAsync(context.ProjectId, context.ConversationId, variantId, expectedRevision, scene, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = variantId, revision = expectedRevision, summary = $"Validated {scene.Objects.Count} composition object(s).", stageId = stage.Id, stage.ExpiresAt, diagnosticCounts = new { errors = 0, warnings = 0 } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = variantId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact composition, preserve unrelated objects, and stage a new scene once." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "INVALID_SCENE", targetId = variantId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionStageAsync(PublishAssistantContext context, Guid editionId, Guid stageId, long expectedRevision)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var variant = await service.ApplyStageAsync(context.ProjectId, context.ConversationId, stageId, expectedRevision, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = variant.Id, editionId, revision = variant.Revision, summary = "Staged composition applied.", changedIds = new[] { variant.Id }, mutation = new { kind = "pageCompositionVariant", id = variant.Id } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a replacement stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionSemanticAsync(PublishAssistantContext context, Guid compositionId, long expectedRevision, ManuscriptOperationInput[] operations)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var stage = await service.StageSemanticOperationsAsync(context.ProjectId, context.ConversationId, compositionId, expectedRevision, operations, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = compositionId, revision = expectedRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {operations.Length} semantic operation(s)." }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the bounded composition and submit a replacement stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "SEMANTIC_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionSemanticStageAsync(PublishAssistantContext context, Guid editionId, Guid stageId, long expectedRevision)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var result = await service.ApplySemanticStageAsync(context.ProjectId, context.ConversationId, stageId, expectedRevision, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = result.Composition.Id, editionId, revision = result.Composition.Revision, changedIds = result.ChangedBlockIds, summary = "Staged Designed Page content applied.", mutation = new { kind = "pageComposition", id = result.Composition.Id } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread and submit a new non-replayed stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task<string> StageCompositionWorkspaceAsync(PublishAssistantContext context, Guid compositionId, long expectedCompositionRevision, Guid variantId, long expectedVariantRevision, ManuscriptOperationInput[] semanticOperations, CompositionScene scene)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var stage = await service.StageWorkspaceAsync(context.ProjectId, context.ConversationId, compositionId, expectedCompositionRevision, variantId, expectedVariantRevision, semanticOperations, scene, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = compositionId, revision = expectedCompositionRevision, stageId = stage.Id, stage.ExpiresAt, summary = $"Validated {semanticOperations.Length} semantic operation(s) with {scene.Objects.Count} scene object(s)." }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = compositionId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit one replacement stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = compositionId, summary = ex.Message }); }
    }

    private async Task<string> ApplyCompositionWorkspaceStageAsync(PublishAssistantContext context, Guid editionId, Guid stageId, long expectedCompositionRevision)
    {
        try { var service = compositions ?? throw new InvalidOperationException("Publication composition tools are unavailable."); var result = await service.ApplyWorkspaceStageAsync(context.ProjectId, context.ConversationId, stageId, expectedCompositionRevision, context.TurnCancellationToken); return Serialize(new { ok = true, targetId = result.Composition.Id, editionId, revision = result.Composition.Revision, variantId = result.Variant.Id, variantRevision = result.Variant.Revision, changedIds = result.ChangedBlockIds, summary = "Designed Page content and layout applied atomically.", mutation = new { kind = "pageComposition", id = result.Composition.Id, selectId = result.Variant.Id } }); }
        catch (CompositionRevisionConflictException ex) { return Serialize(new { ok = false, code = "REVISION_CONFLICT", targetId = stageId, currentRevision = ex.ActualRevision, summary = ex.Message, recovery = "Reread the compact workspace and submit a new non-replayed stage." }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = "WORKSPACE_STAGE_REJECTED", targetId = stageId, summary = ex.Message }); }
    }

    private async Task<string> CreateEditionAsync(
        PublishAssistantContext context,
        string name,
        PublicationEditionFormat format,
        PublicationVendor vendor) =>
        Serialize(await editions.CreateAsync(context.ProjectId, new(name, format, vendor)));

    private async Task<string> CloneEditionAsync(
        PublishAssistantContext context,
        Guid editionId,
        string name,
        long expectedRevision) =>
        Serialize(await editions.CloneAsync(context.ProjectId, editionId, name, expectedRevision));

    private async Task<string> UpdateEditionAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionUpdate update) =>
        Serialize(await editions.UpdateAsync(context.ProjectId, editionId, update));

    private async Task<string> SetDefaultAsync(PublishAssistantContext context, Guid editionId, long expectedRevision) =>
        Serialize(await editions.SetDefaultAsync(context.ProjectId, editionId, expectedRevision));

    private async Task<string> ArchiveAsync(PublishAssistantContext context, Guid editionId, long expectedRevision)
    {
        await editions.ArchiveAsync(context.ProjectId, editionId, expectedRevision);
        return """{"status":"archived"}""";
    }

    private async Task<string> SetContentAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionOutlineItemUpdate[] updates,
        long expectedRevision) =>
        Serialize(await editions.SetOutlineSelectionsAsync(context.ProjectId, editionId, updates, expectedRevision));

    private async Task<string> ReorderContentAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionOutlineItemOrder[] orderedItems,
        long expectedRevision) =>
        Serialize(await editions.ReorderOutlineAsync(
            context.ProjectId,
            editionId,
            orderedItems,
            expectedRevision));

    private async Task<string> UpsertMatterAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationMatterInput input,
        long expectedRevision) =>
        Serialize(await editions.UpsertMatterAsync(context.ProjectId, editionId, input, expectedRevision));

    private async Task<string> DeleteMatterAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid matterId,
        long expectedRevision)
    {
        await editions.DeleteMatterAsync(context.ProjectId, editionId, matterId, expectedRevision);
        return """{"status":"deleted"}""";
    }

    private async Task<string> UpsertStyleMappingAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationEditionStyleMappingInput input,
        long expectedRevision) =>
        Serialize(await editions.UpsertStyleMappingAsync(context.ProjectId, editionId, input, expectedRevision));

    private async Task<string> DeleteStyleMappingAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid mappingId,
        long expectedRevision)
    {
        await editions.DeleteStyleMappingAsync(context.ProjectId, editionId, mappingId, expectedRevision);
        return """{"status":"deleted"}""";
    }

    private async Task<string> AddPlacementAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationImagePlacementCreate input,
        long expectedRevision) =>
        Serialize(await editions.AddImagePlacementAsync(
            context.ProjectId,
            editionId,
            input,
            expectedRevision));

    private async Task<string> UpdatePlacementAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid placementId,
        PublicationImagePlacementUpdate input,
        long expectedRevision) =>
        Serialize(await editions.UpdateImagePlacementAsync(
            context.ProjectId,
            editionId,
            placementId,
            input,
            expectedRevision));

    private async Task<string> ReorderPlacementsAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid[] ids,
        long expectedRevision)
    {
        await editions.ReorderImagePlacementsAsync(context.ProjectId, editionId, ids, expectedRevision);
        return """{"status":"reordered"}""";
    }

    private async Task<string> DeletePlacementAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid placementId,
        long expectedRevision)
    {
        await editions.DeleteImagePlacementAsync(context.ProjectId, editionId, placementId, expectedRevision);
        return """{"status":"deleted"}""";
    }

    private async Task<string> CompareAsync(PublishAssistantContext context, Guid left, Guid right) =>
        Serialize(await editions.CompareAsync(context.ProjectId, left, right));

    private async Task<string> ReadAuditAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await editions.GetAuditAsync(context.ProjectId, editionId));

    private async Task<string> ReadMigrationAsync()
    {
        var recoveryState = await recovery.GetStateAsync();
        return Serialize(new
        {
            History = await migrations.GetHistoryAsync(),
            Recovery = new
            {
                recoveryState.RecoveryRequired,
                recoveryState.MigrationName,
                recoveryState.SourceVersion,
                recoveryState.TargetVersion,
                BackupFileName = recoveryState.BackupPath is null
                    ? null
                    : Path.GetFileName(recoveryState.BackupPath),
                recoveryState.Error,
                recoveryState.CreatedAtUtc,
            },
        });
    }

    private async Task<string> RequestRenderAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await renders.RequestAsync(context.ProjectId, editionId));

    private async Task<string> ListRendersAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await renders.ListAsync(context.ProjectId, editionId));

    private async Task<string> ListDownloadsAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize((await renders.ListArtifactsAsync(context.ProjectId, editionId))
            .Where(artifact => artifact.Kind != PublicationArtifactKind.ProofRecord)
            .Select(artifact => DownloadView(context, artifact)));

    private async Task<string> CancelRenderAsync(PublishAssistantContext context, Guid editionId, Guid jobId) =>
        Serialize(await renders.CancelAsync(context.ProjectId, editionId, jobId));

    private async Task<string> ReadPageMapAsync(PublishAssistantContext context, Guid editionId, Guid jobId) =>
        Serialize(await renders.GetPageMapAsync(context.ProjectId, editionId, jobId));

    private async Task<string> CompareRendersAsync(
        PublishAssistantContext context,
        Guid editionId,
        Guid leftJobId,
        Guid rightJobId) =>
        Serialize(await renders.CompareAsync(context.ProjectId, editionId, leftJobId, rightJobId));

    private async Task<string> ReadCoverAsync(
        PublishAssistantContext context,
        Guid editionId,
        int objectStart,
        int objectCount,
        int structureStart,
        int structureCount)
    {
        var cover = await covers.GetAsync(context.ProjectId, editionId);
        var scene = JsonSerializer.Deserialize<CompositionScene>(cover.CompositionSceneJson, ManuscriptCodec.JsonOptions)
            ?? new CompositionScene();
        var start = Math.Max(0, objectStart);
        var take = Math.Clamp(objectCount, 1, 50);
        structureStart = Math.Max(0, structureStart);
        structureCount = Math.Clamp(structureCount, 1, 50);
        var objects = scene.Objects.Skip(start).Take(take).ToList();
        return Serialize(new
        {
            ok = true,
            targetId = editionId,
            revision = cover.Revision,
            cover.Title,
            cover.Subtitle,
            cover.Author,
            cover.SpineText,
            cover.BackCopy,
            cover.BackgroundColor,
            cover.BarcodeMode,
            cover.Template,
            cover.Diagnostics,
            scene.SchemaVersion,
            scene.Surface,
            layers = scene.Layers.Skip(structureStart).Take(structureCount),
            styles = scene.Styles.Skip(structureStart).Take(structureCount),
            guides = scene.Guides.Skip(structureStart).Take(structureCount),
            objects,
            continuation = new { objects = new { start, returned = objects.Count, total = scene.Objects.Count, hasMore = start + objects.Count < scene.Objects.Count, nextObjectStart = start + objects.Count < scene.Objects.Count ? start + objects.Count : (int?)null }, structure = new { start = structureStart, count = structureCount, layerTotal = scene.Layers.Count, styleTotal = scene.Styles.Count, guideTotal = scene.Guides.Count } },
        });
    }

    private async Task<string> PatchCoverElementAsync(PublishAssistantContext context, Guid editionId, long expectedRevision, string targetKind, Guid targetId, CompositionElementPatch patch)
    {
        try { var cover = await covers.PatchElementAsync(context.ProjectId, editionId, expectedRevision, targetKind, targetId, patch, context.TurnCancellationToken); return Serialize(new { ok = true, targetId, revision = cover.Revision, changedIds = new[] { targetId }, summary = $"Patched cover {targetKind} {targetId:N}.", mutation = new { kind = "coverComposition", id = editionId } }); }
        catch (Exception ex) { return Serialize(new { ok = false, code = ex is DbUpdateConcurrencyException ? "REVISION_CONFLICT" : "PATCH_REJECTED", targetId, summary = ex.Message, recovery = "Reread the cover and retry only the intended fields against its current revision." }); }
    }

    private async Task<string> UpdateCoverAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublicationCoverDesignUpdate update) =>
        Serialize(await covers.UpdateAsync(context.ProjectId, editionId, update));

    private async Task<string> ValidateCoverAsync(PublishAssistantContext context, Guid editionId)
    {
        try
        {
            var cover = await covers.GetAsync(context.ProjectId, editionId, context.TurnCancellationToken);
            return Serialize(new { ok = cover.Diagnostics.Count == 0, targetId = editionId, revision = cover.Revision, summary = cover.Diagnostics.Count == 0 ? "Cover validation passed." : $"Cover validation found {cover.Diagnostics.Count} diagnostic(s).", diagnosticCounts = new { errors = cover.Diagnostics.Count, warnings = 0 }, diagnostics = cover.Diagnostics.Take(12) });
        }
        catch (Exception ex) { return Serialize(new { ok = false, code = "VALIDATION_FAILED", targetId = editionId, summary = ex.Message }); }
    }

    private async Task<string> StageCoverCompositionAsync(
        PublishAssistantContext context,
        Guid editionId,
        long expectedRevision,
        CompositionScene scene)
    {
        var stage = await covers.StageSceneAsync(
            context.ProjectId,
            context.ConversationId,
            editionId,
            expectedRevision,
            scene,
            context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = editionId,
            revision = expectedRevision,
            stageId = stage.Id,
            stage.ExpiresAt,
            summary = $"Staged {scene.Objects.Count} cover objects across {scene.Layers.Count} layers.",
        });
    }

    private async Task<string> ApplyCoverCompositionStageAsync(
        PublishAssistantContext context,
        Guid stageId,
        long expectedRevision)
    {
        var cover = await covers.ApplySceneStageAsync(
            context.ProjectId,
            context.ConversationId,
            stageId,
            expectedRevision,
            context.TurnCancellationToken);
        return Serialize(new
        {
            ok = true,
            targetId = cover.EditionId,
            revision = cover.Revision,
            changedFields = new[] { "compositionScene" },
            diagnosticCount = cover.Diagnostics.Count,
            diagnostics = cover.Diagnostics.Take(5),
            mutation = new { kind = "coverComposition", id = cover.EditionId, selectId = cover.EditionId },
        });
    }

    private async Task<string> PreflightAsync(PublishAssistantContext context, Guid editionId) =>
        Serialize(await packages.PreflightAsync(context.ProjectId, editionId));

    private async Task<string> ExportAsync(
        PublishAssistantContext context,
        Guid editionId,
        PublishExportFormat format)
    {
        var file = await publishing.ExportAsync(context.ProjectId, editionId, format);
        var workspace = await publishing.GetWorkspaceAsync(context.ProjectId, editionId);
        return Serialize(new
        {
            file.FileName,
            file.ContentType,
            workspace.SourceFingerprint,
            RegeneratedAtDownload = true,
            DownloadUrl = $"/projects/{context.ProjectId:N}/publish/editions/{editionId:N}/exports/{format}",
        });
    }

    private async Task<string> BuildPackageAsync(PublishAssistantContext context, Guid editionId)
    {
        var result = await packages.BuildAsync(context.ProjectId, editionId);
        return Serialize(new
        {
            result.Preflight,
            Artifacts = result.Artifacts.Select(artifact => DownloadView(context, artifact)),
        });
    }

    internal static object DownloadView(PublishAssistantContext context, PublicationArtifactView artifact) => new
    {
        artifact.Id,
        artifact.Kind,
        artifact.FileName,
        artifact.MediaType,
        artifact.Sha256,
        artifact.ByteLength,
        artifact.PageCount,
        artifact.SourceFingerprint,
        artifact.RendererVersion,
        artifact.ProfileId,
        artifact.CreatedAt,
        artifact.IsLegacy,
        artifact.IsStale,
        State = artifact.IsLegacy ? "Legacy" : artifact.IsStale ? "Stale" : "Current",
        ViewUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifact.Id:N}",
        DownloadUrl = $"/projects/{context.ProjectId:N}/publish/artifacts/{artifact.Id:N}/download",
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static object Continuation(int start, int returned, int total) => new
    {
        start,
        returned,
        total,
        hasMore = start + returned < total,
        nextStart = start + returned < total ? start + returned : (int?)null,
    };
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..Math.Max(0, maximum - 1)] + "…";
}

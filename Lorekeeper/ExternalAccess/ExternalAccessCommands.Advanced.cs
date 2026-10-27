using System.Text.Json;
using Lorekeeper.Chapters;
using Lorekeeper.Composition;
using Lorekeeper.Images;
using Lorekeeper.Ingest;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Publish;
using Lorekeeper.Research;
using Lorekeeper.Search;
using Lorekeeper.VersionHistory.Services;

namespace Lorekeeper.ExternalAccess;

internal sealed partial class ExternalAccessCommands
{
    internal async Task<object> EnableReleaseEditingAsync(EnableReleaseEditingRequest input, CancellationToken ct) =>
        await Service<IEditionContentService>().SetEnabledAsync(input.ProjectId,
            Workspace!.EditionId ?? throw new ArgumentException("Open this workspace on the exact release first."),
            input.ExpectedEditionRevision, enabled: true, confirmDiscard: false, ct);

    internal async Task<object> PreviewChapterAsync(ChapterPreviewToolRequest input, CancellationToken ct)
    {
        var chapter = Workspace!.ChapterId ?? throw new ArgumentException("Open Editor on the exact chapter first.");
        var preview = await Service<IChapterPreviewService>().RenderPageAsync(input.ProjectId, chapter, null, Target,
            new(input.BlockId, input.ChapterPageNumber), ct);
        return new ExternalImageResult(preview.Data, "image/png", new { chapterId = chapter, preview.ChapterPageNumber,
            preview.PhysicalPage, preview.PageCount, preview.PixelWidth, preview.PixelHeight, preview.Diagnostics });
    }

    internal async Task<object> ReadChapterLayoutAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page(await Service<IChapterPreviewService>().LayoutAsync(input.ProjectId,
            Workspace!.ChapterId ?? throw new ArgumentException("Open Editor on the exact chapter first."), Target, ct), input, "read_chapter_layout");

    private static object OutlineMetadata(Act value) => new { value.Id, value.Title, value.Synopsis, value.Order, value.UpdatedAt };
    private static object OutlineMetadata(Chapter value) => new { value.Id, value.ActId, value.Title, value.Synopsis, value.Order, value.UpdatedAt };

    internal async Task<object> UpdateOutlineAsync(OutlineWriteRequest input, CancellationToken ct)
    {
        if (input.Kind == "Act")
        {
            var current = await Service<IActService>().GetAsync(input.ItemId, ct);
            if (current?.ProjectId != input.ProjectId) throw new KeyNotFoundException("Act not found.");
            ContentVersion.Require(input.ExpectedVersion, OutlineMetadata(current));
            var saved = await Service<IActService>().UpdateAsync(input.ItemId, input.Title, input.Synopsis, ct);
            return new { saved.Id, metadataVersion = ContentVersion.For(OutlineMetadata(saved)) };
        }
        if (input.Kind != "Chapter") throw new ArgumentException("Kind must be Act or Chapter.");
        var chapter = await Service<IChapterService>().GetAsync(input.ItemId, ct);
        if (chapter?.ProjectId != input.ProjectId) throw new KeyNotFoundException("Chapter not found.");
        if (input.AssignAct && input.ActId is Guid act && (await Service<IActService>().GetAsync(act, ct))?.ProjectId != input.ProjectId)
            throw new KeyNotFoundException("Act not found in this project.");
        ContentVersion.Require(input.ExpectedVersion, OutlineMetadata(chapter));
        var result = await Service<IChapterService>().UpdateAsync(chapter.Id, input.Title, input.Synopsis,
            input.AssignAct ? new ChapterActAssignment(input.ActId) : null, ct);
        return new { result.Id, metadataVersion = ContentVersion.For(OutlineMetadata(result)) };
    }

    internal async Task<object> ReorderOutlineAsync(OutlineOrderRequest input, CancellationToken ct)
    {
        if (input.Kind == "Act")
        {
            var items = (await Service<IActService>().ListAsync(input.ProjectId, ct)).Select(value => value.Id).ToArray();
            ContentVersion.Require(input.ExpectedVersion, items);
            RequireSameIds(items, input.OrderedIds);
            await Service<IActService>().ReorderAsync(input.ProjectId, input.OrderedIds, ct);
        }
        else if (input.Kind == "Chapter")
        {
            var items = (await Service<IChapterService>().ListAsync(input.ProjectId, ct)).Where(value => value.ActId == input.ActId).Select(value => value.Id).ToArray();
            ContentVersion.Require(input.ExpectedVersion, items);
            RequireSameIds(items, input.OrderedIds);
            await Service<IChapterService>().ReorderAsync(input.ProjectId, input.ActId, input.OrderedIds, ct);
        }
        else throw new ArgumentException("Kind must be Act or Chapter.");
        return new { saved = true, orderVersion = ContentVersion.For(input.OrderedIds) };
    }

    private static void RequireSameIds(Guid[] current, Guid[] requested)
    {
        if (requested.Length != requested.Distinct().Count() || !current.ToHashSet().SetEquals(requested))
            throw new ExternalAccessException("ORDER_CHANGED", "Read the current complete bucket and preserve every existing identity when reordering.");
    }

    internal async Task<object> UpdateRelationshipAsync(RelationshipWriteRequest input, CancellationToken ct)
    {
        var link = (await Service<IEntityService>().ListLinksAsync(input.ProjectId, input.EntityId, ct)).SingleOrDefault(value => value.EdgeId == input.EdgeId)
            ?? throw new KeyNotFoundException("Relationship not found.");
        ContentVersion.Require(input.ExpectedVersion, link);
        await Service<IEntityService>().UpdateLinkAsync(input.ProjectId, input.EdgeId, input.EdgeType, input.Properties, ct);
        return new { input.EdgeId, saved = true };
    }

    internal async Task<object> WebSearchAsync(WebSearchToolRequest input, CancellationToken ct)
    {
        if (!await Service<ISearchProviderService>().HasActiveProviderAsync(ct))
            throw new ExternalAccessException("SEARCH_NOT_CONFIGURED", "Configure a Search provider in Lorekeeper Settings to use external-agent web search.");
        var result = await Service<ISearchProviderService>().SearchAsync(new(input.Query, Math.Clamp(input.Count, 1, 10)), ct);
        var items = new List<object>();
        foreach (var hit in result.Results)
        {
            var candidate = await Service<IWebIngestCandidateService>().CreateFromSearchResultAsync(input.ProjectId, null, null,
                result.ProviderName, input.Query, hit, ct);
            items.Add(new { sourceId = candidate.Id, candidate.Url, candidate.Title, candidate.Snippet });
        }
        return new { result.ProviderName, result.Query, discoveryOnly = true, results = items };
    }

    internal async Task<object> WebReadAsync(WebReadToolRequest input, CancellationToken ct)
    {
        var read = await Service<IWebIngestCandidateService>().ReadUrlAsync(input.ProjectId, null, input.Url, cancellationToken: ct);
        return Page(new { sourceId = read.Candidate.Id, read.Candidate.Url, read.Candidate.FinalUrl, read.Candidate.Title,
            read.Candidate.ContentHash, read.Candidate.Status, text = read.Candidate.ExtractedText, read.Links, read.Images, read.FromCache }, input, "read_webpage");
    }

    internal async Task<object> ReadResearchAsync(ResearchReadRequest input, CancellationToken ct) =>
        Page(await Service<IWebIngestCandidateService>().GetCachedDetailAsync(input.ProjectId, input.SourceId, ct)
            ?? throw new KeyNotFoundException("Research source not found."), input, "read_research_source");

    internal async Task<object> RetainResearchAsync(ResearchSaveRequest input, CancellationToken ct)
    {
        var current = await Service<IWebIngestCandidateService>().GetCachedDetailAsync(input.ProjectId, input.SourceId, ct)
            ?? throw new KeyNotFoundException("Research source not found.");
        if (string.IsNullOrWhiteSpace(current.ExtractedText) || current.ContentHash != input.ExpectedContentHash)
            throw new ExternalAccessException("SOURCE_CHANGED", "Read this source's current complete cached text before retaining it.");
        if (!string.IsNullOrWhiteSpace(input.Rationale)) await Service<IWebIngestCandidateService>().StageAsync(input.SourceId, input.Rationale, ct);
        var job = await Service<IWebIngestCandidateService>().QueueBatchAsync(input.ProjectId, [input.SourceId], null, null, cancellationToken: ct, mode: IngestJobMode.IndexOnly);
        return new { jobId = job.Id, job.Status, mode = "IndexOnly", canonical = false };
    }

    internal async Task<object> SaveFigureAsync(FigureWriteRequest input, CancellationToken ct)
    {
        var chapter = Workspace!.ChapterId ?? throw new ArgumentException("Open Editor on an exact chapter first.");
        return await Service<ManuscriptOperationCommand>().ApplyTypedAsync(input.ProjectId, Target, chapter, input.ExpectedRevision,
            [new SetFigurePresentation(input.BlockId, input.ImageId, input.AltText, input.Decorative, input.Language, input.Presentation, input.AccessibilityRole)], ct);
    }

    internal async Task<object> SavePageSceneAsync(PageSceneWriteRequest input, CancellationToken ct)
    {
        var saved = await Service<IDesignedPageService>().SaveVariantAsync(Target, input.ProjectId, input.VariantId, input.ExpectedRevision, input.Scene, ct);
        return new { saved.Id, saved.Revision, requiresVisualInspection = true };
    }

    internal async Task<object> ApplyPageAsync(PageSemanticWriteRequest input, CancellationToken ct)
    {
        var page = (await Service<IDesignedPageService>().ListAsync(input.ProjectId, Target, ct)).SingleOrDefault(value => value.Content.Id == input.ContentId)
            ?? throw new KeyNotFoundException("Page content not found in this workspace target.");
        RequireOperationPermissions(ManuscriptCodec.Deserialize(page.Content.SemanticManuscriptJson), input.Operations);
        var saved = await Service<IDesignedPageService>().ApplyAuthoringOperationsAsync(Target, input.ProjectId, input.ContentId,
            input.ExpectedRevision, ManuscriptOperationInput.ToOperations(input.Operations), ct);
        return new { contentId = saved.Content.Id, saved.Content.Revision, saved.ChangedBlockIds, requiresReadback = true };
    }

    internal async Task<object> SaveImageMetadataAsync(ImageMetadataWriteRequest input, CancellationToken ct)
    {
        var image = await Service<IProjectImageService>().GetAsync(input.ProjectId, input.ImageId, ct) ?? throw new KeyNotFoundException("Image not found.");
        var version = ImageMetadataVersion(image);
        if (input.ExpectedVersion != version) throw new ContentVersionConflictException(version);
        return ImageMetadata(await Service<IProjectImageService>().UpdateAsync(input.ProjectId, input.ImageId, input.Update, ct));
    }

    internal async Task<object> ResizeImageAsync(ImageResizeToolRequest input, CancellationToken ct)
    {
        var model = await Service<IImageProviderService>().ResolveAsync(cancellationToken: ct);
        return ImageMetadata(await Service<IProjectImageService>().ResizeAsync(input.ProjectId, model.Constraints, input.ImageId, input.Resize, ct));
    }

    internal async Task<object> ReadReviewAsync(WorkspaceRequest input, CancellationToken ct) =>
        Page(await Service<IProjectVersionHistoryService>().GetReviewAsync(input.ProjectId,
            Workspace!.ChapterId is Guid chapter ? [new(chapter, Target)] : null, ct), input, "inspect_pending_review");

    private Task<PublicationCoverDesignView> CoverAsync(Guid projectId, string surface, CancellationToken ct) =>
        Service<PublicationCoverPreviewService>().ReadAsync(projectId, Workspace!.EditionId, surface, ct);

    internal async Task<object> ReadCoverAsync(CoverReadRequest input, CancellationToken ct) =>
        Page(await CoverAsync(input.ProjectId, input.SurfaceRole, ct), input, "read_publication_cover");

    internal async Task<object> SaveCoverAsync(CoverWriteRequest input, CancellationToken ct) => Workspace!.EditionId is Guid edition
        ? await Service<IPublicationCoverService>().SaveSurfaceWorkspaceAsync(input.ProjectId, edition, input.SurfaceRole, input.Update, input.Scene, ct)
        : input.SurfaceRole == "front" ? await Service<IPublicationBookService>().SaveCoverAsync(input.ProjectId,
            input.ExpectedBookRevision ?? throw new ArgumentException("expectedBookRevision is required for the Core cover."), input.Update, input.Scene, ct)
            : throw new ArgumentException("Core has only a front cover.");

    internal async Task<object> PreviewCoverAsync(CoverPreviewRequest input, CancellationToken ct)
    {
        var rendered = await Service<PublicationCoverPreviewService>().RenderAsync(input.ProjectId, Workspace!.EditionId,
            input.SurfaceRole, input.Annotated ? CompositionCanvasPreviewMode.Annotated : CompositionCanvasPreviewMode.Clean, ct);
        var cover = rendered.Cover;
        var preview = rendered.Preview;
        return new ExternalImageResult(preview.Data, "image/png", new { cover.Id, cover.Revision, preview.Mode, preview.Diagnostics },
            $"/external-access/transfers/previews/cover?projectId={input.ProjectId:N}&workspaceId={input.WorkspaceId:N}&surfaceRole={Uri.EscapeDataString(input.SurfaceRole)}&annotated={input.Annotated}&sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(preview.Data))}");
    }

    internal async Task<object> ReadReadinessAsync(WorkspaceRequest input, CancellationToken ct) => Page(new
    {
        runtime = Service<IPublicationRenderService>().GetRuntimeReadiness(),
        release = Workspace!.EditionId is Guid edition ? await Service<IPublicationPackageService>().PreflightAsync(input.ProjectId, edition, ct) : null,
        note = "Readiness is bounded to the installed renderer, selected profile and retained evidence. Preparation never publishes externally.",
    }, input, "read_publication_readiness");

    internal async Task<object> ReadPublicationDocumentAsync(WorkspaceRequest input, CancellationToken ct)
    {
        var document = Workspace!.EditionId is Guid edition ? await Service<IPublishService>().GetDocumentAsync(input.ProjectId, edition, ct)
            : await Service<IPublishService>().GetCoreDocumentAsync(input.ProjectId, ct);
        return Page(new
        {
        document = new { document.EditionId, document.ProjectId, document.DisplayTitle, document.Profile, document.Sections,
            document.PublicationSections, document.Cover, document.CoverSurfaceScenes, document.SourceFingerprint,
            document.NamedStyles, document.Citations, document.BibliographicRecords,
            assets = document.Assets.Select(value => new { value.Id, value.FileName, value.ContentType, value.AltText,
                byteLength = value.Data.Length, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(value.Data)) }),
            fonts = document.Fonts.Select(value => new { value.FamilyId, value.FaceId, value.FamilyKey, value.FamilyName,
                value.FileName, value.ContentType, value.Weight, value.Italic, byteLength = value.Data.Length,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(value.Data)) }) },
        exports = Enum.GetValues<PublishExportFormat>().Select(format => new { format,
            resource = $"/external-access/transfers/exports/{format}?projectId={input.ProjectId:N}&workspaceId={input.WorkspaceId:N}" }),
        }, input, "read_publication_document");
    }

    internal async Task<object> ListArtifactsAsync(WorkspaceRequest input, CancellationToken ct)
    {
        var artifacts = Workspace!.EditionId is Guid edition ? await Service<IPublicationRenderService>().ListArtifactsAsync(input.ProjectId, edition, ct)
            : (await Service<IPublicationRenderService>().ListCoreAsync(input.ProjectId, ct)).SelectMany(value => value.Artifacts).DistinctBy(value => value.Id).ToArray();
        return Page(artifacts.Select(value => new { artifact = value,
            resource = $"/external-access/transfers/artifacts/{value.Id:N}?projectId={input.ProjectId:N}&workspaceId={input.WorkspaceId:N}" }), input, "list_publication_artifacts");
    }

    internal async Task<PublicationArtifact> RequireArtifactAsync(Guid projectId, Guid artifactId, CancellationToken ct)
    {
        var artifact = await Service<IPublicationRenderService>().GetArtifactAsync(projectId, artifactId, ct)
            ?? throw new KeyNotFoundException("Verified publication artifact not found.");
        if (artifact.EditionId != Workspace!.EditionId) throw new KeyNotFoundException("Artifact does not belong to this workspace target.");
        return artifact;
    }

    internal async Task<object> PreviewArtifactAsync(ArtifactPreviewRequest input, CancellationToken ct)
    {
        _ = await RequireArtifactAsync(input.ProjectId, input.ArtifactId, ct);
        var preview = await Service<IPublicationArtifactPreviewService>().RenderPageAsync(input.ProjectId, input.ArtifactId,
            Math.Max(1, input.PageNumber), Math.Clamp(input.WidthPixels, 200, 1200), ct) ?? throw new KeyNotFoundException("Artifact page unavailable.");
        return new ExternalImageResult(preview.Data, "image/png", new { preview.ArtifactSha256, preview.PageNumber, preview.Width, preview.Height });
    }

    internal async Task<object> CancelPublicationAsync(CancelJobRequest input, CancellationToken ct)
    {
        var jobs = await Service<IPublicationPreparationService>().ListAsync(input.ProjectId,
            Target.IsCore ? PublicationTargetKind.CoreBook : PublicationTargetKind.Release, Workspace!.EditionId, ct);
        if (!jobs.Any(value => value.Id == input.JobId)) throw new KeyNotFoundException("Preparation job not found in this target.");
        return await Service<IPublicationPreparationService>().CancelAsync(input.ProjectId, input.JobId, ct);
    }
}

using System.Text.Json;
using Lorekeeper.Authoring;
using Lorekeeper.Composition;
using Lorekeeper.Images;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.VersionHistory.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Publish;

/// <summary>
/// Analyzes publication placements with the managed publication geometry and
/// permanently replaces undersized references with deterministic Lanczos
/// derivatives before the Press renderer is invoked.
/// </summary>
public interface IPublicationImagePreparationService
{
    Task<PublicationImagePreparationResult> PrepareAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        PublicationRenderScope renderScope,
        string capturedFingerprint,
        CancellationToken cancellationToken = default);
}

public sealed class PublicationImagePreparationException(
    string message,
    PublicationImagePreparationSummary summary,
    bool retryable = false,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public PublicationImagePreparationSummary Summary { get; } = summary;
    public bool Retryable { get; } = retryable;
}

// Managed publication evidence deliberately mirrors the placement facts used
// by the publication model. It is not a Press protocol: Press remains an
// artifact renderer and receives only the final, already-referenced assets.
internal sealed record PublicationImagePlacementEvidence(
    Guid AssetId,
    string Surface,
    int? PageNumber,
    double EffectiveDpi,
    double RequiredDpi,
    int RequiredWidthPixels,
    int RequiredHeightPixels,
    bool NeedsUpscale,
    FigureImageFit Fit);

internal sealed record PublicationImageAssetEvidence(
    Guid AssetId,
    int SourceWidthPixels,
    int SourceHeightPixels,
    int RequiredWidthPixels,
    int RequiredHeightPixels,
    bool NeedsUpscale);

internal sealed record PublicationImageAnalysis(
    IReadOnlyList<PublicationImagePlacementEvidence> Placements,
    IReadOnlyList<PublicationImageAssetEvidence> Assets,
    IReadOnlyList<PublicationImagePreparationFailure> Failures);

public sealed class PublicationImagePreparationService(
    IAppDatabaseOperationFactory database,
    IPublicationBookService books,
    IPublicationEditionService editions,
    IPublishService publishing,
    IProjectImageService images,
    IManuscriptService manuscripts,
    IAuthoringDeltaHistoryRuntime authoringHistory,
    ProjectVersionHistoryUiEvents historyEvents,
    IOptions<ProjectImageGenerationOptions> imageOptions) : IPublicationImagePreparationService
{
    private static readonly JsonSerializerOptions JsonOptions = ManuscriptCodec.JsonOptions;

    public async Task<PublicationImagePreparationResult> PrepareAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        PublicationRenderScope renderScope,
        string capturedFingerprint,
        CancellationToken cancellationToken = default)
    {
        var threshold = await ResolveThresholdAsync(projectId, targetKind, editionId, cancellationToken);
        var currentFingerprint = await SourceFingerprintAsync(projectId, targetKind, editionId, renderScope, cancellationToken);
        EnsureFingerprint(capturedFingerprint, currentFingerprint, threshold);

        // EPUB has no raster transformation. It still participates in the
        // captured-fingerprint check so a queued job cannot render stale data.
        if (threshold <= 0)
            return new(SuccessSummary(threshold), currentFingerprint, Committed: false);

        var document = await GetDocumentAsync(projectId, targetKind, editionId, cancellationToken);
        var firstPass = await AnalyzeImagesAsync(
            projectId,
            targetKind,
            editionId,
            renderScope,
            threshold,
            document,
            cancellationToken);
        var failures = ValidateEvidence(firstPass, threshold);
        if (failures.Count > 0)
            return new(FailureSummary(threshold, failures), currentFingerprint, Committed: false);

        var required = firstPass.Assets
            .Where(asset => asset.NeedsUpscale)
            .GroupBy(asset => asset.AssetId)
            .Select(group => group.OrderByDescending(item => (long)item.RequiredWidthPixels * item.RequiredHeightPixels).First())
            .ToList();
        if (required.Count == 0)
        {
            var unchangedFingerprint = await SourceFingerprintAsync(projectId, targetKind, editionId, renderScope, cancellationToken);
            EnsureFingerprint(capturedFingerprint, unchangedFingerprint, threshold);
            return new(SuccessSummary(threshold), unchangedFingerprint, Committed: false);
        }
        if (!imageOptions.Value.PrintUpscale)
        {
            return new(
                FailureSummary(
                    threshold,
                    [new(
                        "IMAGE_UPSCALE_DISABLED",
                        "One or more publication images are below the required DPI, and automatic image upscaling is disabled. Enable Images:PrintUpscale or replace the undersized images.")]),
                currentFingerprint,
                Committed: false);
        }

        var created = 0;
        var reused = 0;
        var historyTargets = new List<string>();
        var changedChapters = new List<(EditorContentTarget Target, Guid ChapterId)>();
        var replacedReferences = 0;
        var replacementIds = new Dictionary<Guid, Guid>();
        await using (var operation = await database.OpenWriteAsync(projectId, cancellationToken))
        {
            operation.ShareWithNestedOperations();
            var db = operation.Db;
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var beforeCommitFingerprint = await SourceFingerprintAsync(projectId, targetKind, editionId, renderScope, cancellationToken);
                EnsureFingerprint(capturedFingerprint, beforeCommitFingerprint, threshold);

                foreach (var asset in required)
                {
                    ProjectImagePrintUpscaleResult derivative;
                    try
                    {
                        ValidateRaster(asset.RequiredWidthPixels, asset.RequiredHeightPixels, asset.AssetId);
                        var request = new ProjectImagePrintUpscaleRequest(
                            asset.RequiredWidthPixels,
                            asset.RequiredHeightPixels,
                            asset.RequiredWidthPixels / threshold,
                            asset.RequiredHeightPixels / threshold,
                            threshold,
                            "publication-preparation");
                        derivative = await images.EnsurePrintUpscaleAsync(
                            projectId,
                            asset.AssetId,
                            request,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException)
                    {
                        var code = IsRasterLimitFailure(exception)
                            ? "IMAGE_UPSCALE_RASTER_TOO_LARGE"
                            : exception.Message.Contains("decode", StringComparison.OrdinalIgnoreCase)
                                ? "IMAGE_CORRUPT"
                                : "IMAGE_UPSCALE_FAILED";
                        throw new PublicationImagePreparationException(
                            exception.Message,
                            FailureSummary(threshold, [new(code, exception.Message, asset.AssetId)]),
                            innerException: exception);
                    }
                    replacementIds[asset.AssetId] = derivative.Image.Id;
                    if (derivative.WasCreated) created++;
                    else reused++;
                }

                // Cancellation is checked immediately before the only save
                // that can make the reference replacement visible. If it is
                // requested here, disposing the transaction rolls everything
                // back, including derivatives created above.
                cancellationToken.ThrowIfCancellationRequested();
                replacedReferences = await ReplaceReferencesAsync(
                    db,
                    projectId,
                    targetKind,
                    editionId,
                    renderScope,
                    document,
                    replacementIds,
                    historyTargets,
                    changedChapters,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await db.SaveChangesAsync(cancellationToken);
                // Once the save has succeeded, commit with a non-cancellable
                // token so cancellation cannot leave the transaction in an
                // ambiguous state after the replacement is durable.
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }

        var afterCommitFingerprint = await SourceFingerprintAsync(projectId, targetKind, editionId, renderScope, CancellationToken.None);
        var postFailures = new List<PublicationImagePreparationFailure>();
        try
        {
            var postDocument = await GetDocumentAsync(
                projectId,
                targetKind,
                editionId,
                CancellationToken.None);
            var secondPass = await AnalyzeImagesAsync(
                projectId,
                targetKind,
                editionId,
                renderScope,
                threshold,
                postDocument,
                CancellationToken.None);
            postFailures.AddRange(ValidateEvidence(secondPass, threshold));
            if (secondPass.Placements.Any(item => item.NeedsUpscale))
            {
                postFailures.Add(new(
                    "IMAGE_STILL_BELOW_THRESHOLD",
                    "One or more publication placements remain below the required image resolution after preparation."));
            }
            var postAnalysisFingerprint = await SourceFingerprintAsync(
                projectId,
                targetKind,
                editionId,
                renderScope,
                CancellationToken.None);
            if (!string.Equals(postAnalysisFingerprint, afterCommitFingerprint, StringComparison.Ordinal))
            {
                postFailures.Add(new(
                    "PUBLICATION_SOURCE_STALE",
                    "The publication changed while image preparation was validating its permanent replacements. Retry preparation.",
                    Retryable: true));
            }
            afterCommitFingerprint = postAnalysisFingerprint;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException)
        {
            postFailures.Add(new(
                exception.Message.Contains("decode", StringComparison.OrdinalIgnoreCase)
                    ? "IMAGE_CORRUPT"
                    : "IMAGE_PREPARATION_FAILED",
                exception.Message));
        }

        foreach (var target in historyTargets.Distinct(StringComparer.Ordinal))
            authoringHistory.Clear(target);
        foreach (var (target, chapterId) in changedChapters.Distinct())
            await manuscripts.RefreshDerivedStateAsync(target, chapterId, CancellationToken.None);
        historyEvents.PublishReviewStateChanged(projectId);

        return new(
            new(created, reused, replacedReferences, threshold, postFailures),
            afterCommitFingerprint,
            Committed: true);
    }

    private async Task<double> ResolveThresholdAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        CancellationToken cancellationToken)
    {
        if (targetKind == PublicationTargetKind.CoreBook)
            return 180;
        if (editionId is not Guid id)
            throw new ArgumentException("A release image preparation requires an edition ID.", nameof(editionId));
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var edition = await operation.Db.PublicationEditions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Publication release not found.");
        return edition.Format == PublicationEditionFormat.Epub ? 0 : edition.Format == PublicationEditionFormat.DigitalPdf ? 180 : 300;
    }

    private async Task<string> SourceFingerprintAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        PublicationRenderScope renderScope,
        CancellationToken cancellationToken) =>
        targetKind == PublicationTargetKind.CoreBook
            ? await books.GetSourceFingerprintAsync(projectId, cancellationToken)
            : renderScope switch
            {
                PublicationRenderScope.Interior => await editions.GetInteriorFingerprintAsync(projectId, editionId!.Value, cancellationToken),
                PublicationRenderScope.Cover => await editions.GetCoverFingerprintAsync(
                    projectId,
                    editionId!.Value,
                    await CurrentInteriorPageCountAsync(projectId, editionId.Value, cancellationToken),
                    cancellationToken),
                _ => await editions.GetSourceFingerprintAsync(projectId, editionId!.Value, cancellationToken),
            };

    private async Task<int> CurrentInteriorPageCountAsync(
        Guid projectId,
        Guid editionId,
        CancellationToken cancellationToken)
    {
        var interiorFingerprint = await editions.GetInteriorFingerprintAsync(
            projectId,
            editionId,
            cancellationToken);
        await using var operation = await database.OpenReadAsync(cancellationToken);
        var pageCount = await operation.Db.PublicationArtifacts.AsNoTracking()
            .Where(item => item.ProjectId == projectId
                && item.EditionId == editionId
                && item.Kind == PublicationArtifactKind.InteriorPdf
                && !item.IsLegacy
                && item.SourceFingerprint == interiorFingerprint
                && item.RenderJob != null
                && !item.RenderJob.IsLegacy
                && item.RenderJob.Scope == PublicationRenderScope.Interior
                && item.RenderJob.Status == PublicationRenderStatus.Completed
                && item.PageCount != null)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => item.PageCount!.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return pageCount > 0
            ? pageCount
            : throw new InvalidOperationException("Prepare the current interior before preparing cover images.");
    }

    private static void EnsureFingerprint(string captured, string current, double threshold)
    {
        if (string.Equals(captured, current, StringComparison.Ordinal))
            return;
        var summary = FailureSummary(
            threshold,
            [new(
                "PUBLICATION_SOURCE_STALE",
                "The publication changed while image preparation was queued. Retry preparation to analyze the current source.",
                Retryable: true)]);
        throw new PublicationImagePreparationException(summary.Failures[0].Message, summary, retryable: true);
    }

    private async Task<PublishDocument> GetDocumentAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        CancellationToken cancellationToken) =>
        targetKind == PublicationTargetKind.CoreBook
            ? await publishing.GetCoreDocumentAsync(projectId, cancellationToken)
            : await publishing.GetDocumentAsync(projectId, editionId!.Value, cancellationToken);

    private async Task<PublicationImageAnalysis> AnalyzeImagesAsync(
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        PublicationRenderScope renderScope,
        double threshold,
        PublishDocument document,
        CancellationToken cancellationToken)
    {
        var maximumAssetBytes = ConfiguredMaximumAssetBytes();
        var assets = document.Assets
            .Append(document.CoverAsset)
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var dimensions = new Dictionary<Guid, (int Width, int Height)>();
        var aggregate = new Dictionary<Guid, MutableImageAssetEvidence>();
        var placements = new List<PublicationImagePlacementEvidence>();
        var failures = new List<PublicationImagePreparationFailure>();
        var failedAssets = new HashSet<Guid>();

        void AddFailure(
            string code,
            string message,
            Guid? assetId = null,
            string? surface = null,
            int? page = null)
        {
            if (assetId is Guid id && !failedAssets.Add(id))
                return;
            failures.Add(new(code, message, assetId, surface, page));
        }

        bool TryGetDimensions(Guid assetId, string surface, int? page, out (int Width, int Height) size)
        {
            if (dimensions.TryGetValue(assetId, out size))
                return true;
            if (!assets.TryGetValue(assetId, out var asset))
            {
                AddFailure("IMAGE_MISSING", $"Publication placement references missing image {assetId:N}.", assetId, surface, page);
                size = default;
                return false;
            }
            if (asset.Data.LongLength == 0 || asset.Data.LongLength > maximumAssetBytes)
            {
                AddFailure("IMAGE_ASSET_TOO_LARGE", $"Image '{asset.FileName}' exceeds the configured publication asset limit of {maximumAssetBytes / (1024d * 1024d):0.##} MiB.", assetId, surface, page);
                size = default;
                return false;
            }
            if (asset.ContentType is not ("image/png" or "image/jpeg" or "image/jpg"))
            {
                AddFailure("IMAGE_FORMAT_UNSUPPORTED", $"Image '{asset.FileName}' must be PNG or JPEG for publication.", assetId, surface, page);
                size = default;
                return false;
            }
            try
            {
                using var bitmap = SKBitmap.Decode(asset.Data);
                if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                    throw new InvalidDataException("Image data could not be decoded.");
                size = (bitmap.Width, bitmap.Height);
                dimensions[assetId] = size;
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                AddFailure("IMAGE_CORRUPT", $"Image '{asset.FileName}' could not be decoded: {exception.Message}", assetId, surface, page);
                size = default;
                return false;
            }
        }

        void AddOccurrence(
            Guid assetId,
            string surface,
            int? page,
            double widthPoints,
            double heightPoints,
            FigureImageFit fit)
        {
            if (!TryGetDimensions(assetId, surface, page, out var source))
                return;
            if (source.Width > LayoutImageSizeResolver.PrintMaximumEdge
                || source.Height > LayoutImageSizeResolver.PrintMaximumEdge
                || (long)source.Width * source.Height > LayoutImageSizeResolver.PrintMaximumPixels)
            {
                AddFailure(
                    "IMAGE_SOURCE_RASTER_TOO_LARGE",
                    $"Image {assetId:N} is {source.Width}x{source.Height}, exceeding the publication limit of {LayoutImageSizeResolver.PrintMaximumEdge:N0} pixels per edge and {LayoutImageSizeResolver.PrintMaximumPixels:N0} pixels.",
                    assetId,
                    surface,
                    page);
                return;
            }
            try
            {
                var placement = ResolvePlacement(
                    assetId,
                    surface,
                    page,
                    widthPoints,
                    heightPoints,
                    fit,
                    source.Width,
                    source.Height,
                    threshold);
                placements.Add(placement);
                if (!aggregate.TryGetValue(assetId, out var current))
                {
                    current = new MutableImageAssetEvidence(assetId, source.Width, source.Height);
                    aggregate.Add(assetId, current);
                }
                current.RequiredWidthPixels = Math.Max(current.RequiredWidthPixels, placement.RequiredWidthPixels);
                current.RequiredHeightPixels = Math.Max(current.RequiredHeightPixels, placement.RequiredHeightPixels);
                current.NeedsUpscale |= placement.NeedsUpscale;
            }
            catch (InvalidOperationException exception)
            {
                AddFailure("IMAGE_PLACEMENT_INVALID", exception.Message, assetId, surface, page);
            }
        }

        if (renderScope != PublicationRenderScope.Cover)
        foreach (var chapter in document.Sections.SelectMany(section => section.Chapters))
        {
            foreach (var block in chapter.Manuscript.Content.Where(item => item.Type == ManuscriptBlockType.Figure && item.ImageId is not null))
            {
                var presentation = block.FigurePresentation ?? new FigurePresentation();
                var dimensionsForFigure = FlowingFigureDimensions(document.Profile, presentation);
                AddOccurrence(
                    block.ImageId!.Value,
                    "figure",
                    null,
                    dimensionsForFigure.WidthPoints,
                    dimensionsForFigure.HeightPoints,
                    presentation.Fit);
            }
            foreach (var composition in chapter.DesignedPages)
                foreach (var variant in composition.Variants)
                    AddSceneOccurrences(variant.Scene, "designed-page", null, AddOccurrence);
        }

        if (renderScope != PublicationRenderScope.Cover)
        foreach (var section in document.PublicationSections)
        {
            foreach (var block in section.Manuscript.Content.Where(item => item.Type == ManuscriptBlockType.Figure && item.ImageId is not null))
            {
                var presentation = block.FigurePresentation ?? new FigurePresentation();
                var dimensionsForFigure = FlowingFigureDimensions(document.Profile, presentation);
                AddOccurrence(
                    block.ImageId!.Value,
                    "publication-section",
                    null,
                    dimensionsForFigure.WidthPoints,
                    dimensionsForFigure.HeightPoints,
                    presentation.Fit);
            }
            foreach (var composition in section.DesignedPages)
                foreach (var variant in composition.Variants)
                    AddSceneOccurrences(variant.Scene, "publication-section", null, AddOccurrence);
        }

        if (renderScope != PublicationRenderScope.Interior && document.Cover is not null)
        {
            AddSceneOccurrences(
                document.Cover.Scene,
                "cover",
                null,
                AddOccurrence,
                fallbackWidthPoints: document.Profile.PageWidthInches * 72,
                fallbackHeightPoints: document.Profile.PageHeightInches * 72);
        }

        if (renderScope != PublicationRenderScope.Interior && document.CoverAsset is { } coverAsset)
            AddOccurrence(
                coverAsset.Id,
                "selected-cover",
                null,
                document.Profile.PageWidthInches * 72,
                document.Profile.PageHeightInches * 72,
                FigureImageFit.Cover);

        if (renderScope != PublicationRenderScope.Interior)
        foreach (var (surface, scene) in document.CoverSurfaceScenes)
            AddSceneOccurrences(scene, surface, null, AddOccurrence);

        return new(
            placements,
            aggregate.Values.Select(item => item.ToEvidence()).ToList(),
            failures);
    }

    private static (double WidthPoints, double HeightPoints) FlowingFigureDimensions(
        PublishDocumentProfile profile,
        FigurePresentation presentation)
    {
        var (width, height) = LayoutImageSizeResolver.ResolveFlowingFigurePhysicalSize(
            profile.PageWidthInches,
            profile.PageHeightInches,
            profile.PageMarginInches,
            presentation,
            profile.BleedInches);
        return (width * 72, height * 72);
    }

    private static void AddSceneOccurrences(
        CompositionScene scene,
        string surface,
        int? page,
        Action<Guid, string, int?, double, double, FigureImageFit> addOccurrence,
        double? fallbackWidthPoints = null,
        double? fallbackHeightPoints = null)
    {
        var surfaceWidth = scene.Surface.WidthPoints > 0 ? scene.Surface.WidthPoints : fallbackWidthPoints ?? 0;
        var surfaceHeight = scene.Surface.HeightPoints > 0 ? scene.Surface.HeightPoints : fallbackHeightPoints ?? 0;
        foreach (var item in CompositionSceneResolver.Flatten(scene).Where(item =>
            item.Visible
            && scene.Layers.Any(layer => layer.Id == item.LayerId && layer.Visible)
            && item.Kind == CompositionObjectKind.Image
            && item.ImageId is not null))
        {
            addOccurrence(
                item.ImageId!.Value,
                surface,
                page,
                surfaceWidth * item.Bounds.WidthPercent / 100,
                surfaceHeight * item.Bounds.HeightPercent / 100,
                item.ImageFit);
        }
    }

    private static PublicationImagePlacementEvidence ResolvePlacement(
        Guid assetId,
        string surface,
        int? page,
        double widthPoints,
        double heightPoints,
        FigureImageFit fit,
        int sourceWidth,
        int sourceHeight,
        double threshold)
    {
        if (!double.IsFinite(widthPoints) || !double.IsFinite(heightPoints) || widthPoints <= 0 || heightPoints <= 0)
            throw new InvalidOperationException("Image placement has invalid physical bounds.");
        var widthScale = widthPoints / sourceWidth;
        var heightScale = heightPoints / sourceHeight;
        var scale = fit == FigureImageFit.Contain
            ? Math.Min(widthScale, heightScale)
            : Math.Max(widthScale, heightScale);
        if (!double.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("Image placement has invalid scale.");
        var requiredScale = scale * threshold / 72;
        var requiredWidth = CeilingRaster((double)sourceWidth * requiredScale);
        var requiredHeight = CeilingRaster((double)sourceHeight * requiredScale);
        return new(
            assetId,
            surface,
            page,
            72 / scale,
            threshold,
            requiredWidth,
            requiredHeight,
            requiredWidth > sourceWidth || requiredHeight > sourceHeight,
            fit);
    }

    private static int CeilingRaster(double value)
    {
        try
        {
            return LayoutImageSizeResolver.CeilingRasterDimension(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Image placement requires an invalid raster size.", exception);
        }
    }

    private static bool IsRasterLimitFailure(Exception exception) =>
        exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("exceed", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("too large", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("maximum edge", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("maximum pixels", StringComparison.OrdinalIgnoreCase);

    private sealed class MutableImageAssetEvidence(Guid assetId, int sourceWidthPixels, int sourceHeightPixels)
    {
        public Guid AssetId { get; } = assetId;
        public int SourceWidthPixels { get; } = sourceWidthPixels;
        public int SourceHeightPixels { get; } = sourceHeightPixels;
        public int RequiredWidthPixels { get; set; }
        public int RequiredHeightPixels { get; set; }
        public bool NeedsUpscale { get; set; }

        public PublicationImageAssetEvidence ToEvidence() => new(
            AssetId,
            SourceWidthPixels,
            SourceHeightPixels,
            RequiredWidthPixels,
            RequiredHeightPixels,
            NeedsUpscale);
    }

    private static List<PublicationImagePreparationFailure> ValidateEvidence(
        PublicationImageAnalysis evidence,
        double threshold)
    {
        var failures = evidence.Failures.ToList();
        foreach (var asset in evidence.Assets)
        {
            if (asset.SourceWidthPixels <= 0 || asset.SourceHeightPixels <= 0)
            {
                failures.Add(new("IMAGE_CORRUPT", $"Image {asset.AssetId:N} has invalid raster dimensions.", asset.AssetId));
                continue;
            }
            if (asset.NeedsUpscale)
            {
                try { ValidateRaster(asset.RequiredWidthPixels, asset.RequiredHeightPixels, asset.AssetId); }
                catch (InvalidOperationException exception)
                {
                    failures.Add(new("IMAGE_UPSCALE_RASTER_TOO_LARGE", exception.Message, asset.AssetId));
                }
            }
        }

        foreach (var placement in evidence.Placements)
        {
            if (placement.EffectiveDpi + .01 >= placement.RequiredDpi)
                continue;
            if (placement.NeedsUpscale)
            {
                try { ValidateRaster(placement.RequiredWidthPixels, placement.RequiredHeightPixels, placement.AssetId); }
                catch (InvalidOperationException exception)
                {
                    failures.Add(new(
                        "IMAGE_UPSCALE_RASTER_TOO_LARGE",
                        $"{exception.Message} Placement: {placement.Surface}{(placement.PageNumber is int page ? $" page {page}" : string.Empty)}.",
                        placement.AssetId,
                        placement.Surface,
                        placement.PageNumber));
                }
            }
            else
            {
                failures.Add(new(
                    "IMAGE_LOW_DPI",
                    $"Image placement on {placement.Surface}{(placement.PageNumber is int page ? $" page {page}" : string.Empty)} is below {placement.RequiredDpi:0.##} DPI.",
                    placement.AssetId,
                    placement.Surface,
                    placement.PageNumber));
            }
        }

        return failures;
    }

    private static void ValidateRaster(int width, int height, Guid assetId)
    {
        if (width <= 0 || height <= 0
            || width > LayoutImageSizeResolver.PrintMaximumEdge
            || height > LayoutImageSizeResolver.PrintMaximumEdge
            || (long)width * height > LayoutImageSizeResolver.PrintMaximumPixels)
            throw new InvalidOperationException(
                $"Image {assetId:N} requires a {width}x{height} raster, which exceeds Lorekeeper's publication limit of "
                + $"{LayoutImageSizeResolver.PrintMaximumEdge} pixels per edge and {LayoutImageSizeResolver.PrintMaximumPixels:N0} pixels.");
    }

    private long ConfiguredMaximumAssetBytes()
    {
        const int pressMaximumBytes = 256 * 1024 * 1024;
        var configured = imageOptions.Value.MaxPrintUpscaleBytes;
        return configured > 0 ? Math.Min(configured, pressMaximumBytes) : pressMaximumBytes;
    }

    private static PublicationImagePreparationSummary SuccessSummary(double threshold) =>
        new(0, 0, 0, threshold, []);

    private static PublicationImagePreparationSummary FailureSummary(
        double threshold,
        IReadOnlyList<PublicationImagePreparationFailure> failures) =>
        new(0, 0, 0, threshold, failures);

    private static async Task<int> ReplaceReferencesAsync(
        AppDbContext db,
        Guid projectId,
        PublicationTargetKind targetKind,
        Guid? editionId,
        PublicationRenderScope renderScope,
        PublishDocument document,
        IReadOnlyDictionary<Guid, Guid> replacements,
        ICollection<string> historyTargets,
        ICollection<(EditorContentTarget Target, Guid ChapterId)> changedChapters,
        CancellationToken cancellationToken)
    {
        // The effective publish document supplies the owner identity. An
        // inherited release row resolves to its Core row, while an existing
        // override resolves to that release row; no customization is created
        // here. All owners are changed under the caller's project lease and
        // transaction, and the caller refreshes manuscript projections and
        // invalidates manual history after the transaction commits.
        var changed = 0;
        var coreChanged = false;
        var releaseChanged = false;
        var now = DateTime.UtcNow;

        var chapterIds = renderScope == PublicationRenderScope.Cover
            ? []
            : document.Sections.SelectMany(item => item.Chapters).Select(item => item.Id).Distinct().ToList();
        var chapters = await db.Chapters.Where(item => item.ProjectId == projectId && chapterIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var edition = targetKind == PublicationTargetKind.Release
            ? await db.PublicationEditions.SingleAsync(item => item.ProjectId == projectId && item.Id == editionId, cancellationToken)
            : null;
        var overrides = edition?.EditionSpecificContentEnabled == true
            ? await db.PublicationEditionChapterOverrides.Where(item => item.EditionId == editionId && chapterIds.Contains(item.ChapterId)).ToDictionaryAsync(item => item.ChapterId, cancellationToken)
            : new Dictionary<Guid, PublicationEditionChapterOverride>();
        foreach (var chapterDocument in document.Sections.SelectMany(item => item.Chapters))
        {
            var chapterOverride = targetKind == PublicationTargetKind.Release
                ? overrides.GetValueOrDefault(chapterDocument.Id)
                : null;
            var chapter = chapterOverride is null ? chapters.GetValueOrDefault(chapterDocument.Id) : null;
            if (chapterOverride is null && chapter is null)
                continue;
            var currentJson = chapterOverride?.ManuscriptJson ?? chapter!.ManuscriptJson;
            var currentRevision = chapterOverride?.Revision ?? chapter!.ManuscriptRevision;
            var current = ManuscriptCodec.Deserialize(currentJson, chapterDocument.Id, currentRevision);
            var (updated, count) = ReplaceManuscriptImages(current, replacements);
            if (count == 0)
                continue;
            var nextRevision = checked(currentRevision + 1);
            var serialized = ManuscriptCodec.Serialize(updated with { Revision = nextRevision });
            if (chapter is not null)
            {
                chapter.ManuscriptJson = serialized;
                chapter.ManuscriptRevision++;
                chapter.UpdatedAt = now;
                chapter.VectorIndexState = VectorIndexState.Stale;
                historyTargets.Add($"chapter:{chapter.Id:D}");
                changedChapters.Add((EditorContentTarget.Core, chapter.Id));
                coreChanged = true;
            }
            else
            {
                chapterOverride!.ManuscriptJson = serialized;
                chapterOverride.Revision++;
                chapterOverride.UpdatedAt = now;
                historyTargets.Add($"release:{editionId:D}:chapter:{chapterOverride.ChapterId:D}");
                changedChapters.Add((EditorContentTarget.ForEdition(editionId!.Value), chapterOverride.ChapterId));
                releaseChanged = true;
            }
            changed += count;
        }

        var sectionIds = renderScope == PublicationRenderScope.Cover
            ? []
            : document.PublicationSections.Select(item => item.Id).Distinct().ToList();
        var sections = await db.PublicationSections.Where(item => item.ProjectId == projectId && sectionIds.Contains(item.Id)).ToListAsync(cancellationToken);
        foreach (var section in sections)
        {
            var current = ManuscriptCodec.Deserialize(section.ManuscriptJson, section.Id, section.Revision);
            var (updated, count) = ReplaceManuscriptImages(current, replacements);
            if (count == 0)
                continue;
            section.Revision++;
            section.ManuscriptJson = ManuscriptCodec.Serialize(updated with { ManuscriptId = section.Id, Revision = section.Revision });
            section.UpdatedAt = now;
            historyTargets.Add(section.EditionId is Guid sectionEditionId
                ? $"release:{sectionEditionId:D}:section:{section.Id:D}"
                : $"publication-section:{section.Id:D}");
            if (section.EditionId is null) coreChanged = true;
            else releaseChanged = true;
            changed += count;
        }

        var chapterVariantOwners = document.Sections.SelectMany(item => item.Chapters)
            .SelectMany(chapter => chapter.DesignedPages
                .SelectMany(composition => composition.Variants.Select(variant => (variant.Id, ChapterId: chapter.Id))))
            .ToDictionary(item => item.Id, item => item.ChapterId);
        var variants = renderScope == PublicationRenderScope.Cover
            ? []
            : document.Sections.SelectMany(item => item.Chapters).SelectMany(item => item.DesignedPages)
                .Concat(document.PublicationSections.SelectMany(item => item.DesignedPages))
                .SelectMany(item => item.Variants).Select(item => item.Id).Distinct().ToList();
        var variantRows = await db.DesignedPageVariants
            .Include(item => item.Content).ThenInclude(item => item.Page)
            .Where(item => variants.Contains(item.Id))
            .ToListAsync(cancellationToken);
        foreach (var variant in variantRows)
        {
            var scene = JsonSerializer.Deserialize<CompositionScene>(variant.SceneJson, JsonOptions)
                ?? throw new InvalidDataException($"Page composition variant {variant.Id:N} has no scene.");
            var (updated, count) = ReplaceSceneImages(scene, replacements);
            if (count == 0)
                continue;
            variant.Revision++;
            variant.SceneJson = JsonSerializer.Serialize(updated, JsonOptions);
            variant.UpdatedAt = now;
            historyTargets.Add(variant.Content.EditionId is Guid contentEditionId
                ? $"release:{contentEditionId:D}:designed-page-content:{variant.ContentId:D}"
                : $"designed-page-content:{variant.ContentId:D}");
            if (variant.Content.EditionId is Guid variantEditionId)
            {
                releaseChanged = true;
                if (chapterVariantOwners.TryGetValue(variant.Id, out var chapterId))
                    changedChapters.Add((EditorContentTarget.ForEdition(variantEditionId), chapterId));
            }
            else
            {
                coreChanged = true;
                if (chapterVariantOwners.TryGetValue(variant.Id, out var chapterId))
                    changedChapters.Add((EditorContentTarget.Core, chapterId));
            }
            changed += count;
        }

        var book = await db.PublicationBooks.Include(item => item.CoverDesign)
            .SingleAsync(item => item.ProjectId == projectId, cancellationToken);
        if (renderScope != PublicationRenderScope.Interior
            && targetKind == PublicationTargetKind.Release && edition!.SelectedCoverImageId is Guid selected
            && replacements.TryGetValue(selected, out var selectedReplacement))
        {
            edition.SelectedCoverImageId = selectedReplacement;
            changed++;
            releaseChanged = true;
        }

        var useCoreCover = targetKind == PublicationTargetKind.CoreBook || edition?.InheritsCoreCover == true;
        if (renderScope != PublicationRenderScope.Interior && useCoreCover)
        {
            if (book.CoverDesign is not null)
            {
                var count = ReplaceCoverDesign(
                    book.CoverDesign.CompositionSceneJson,
                    null,
                    replacements,
                    out var sceneJson,
                    out _,
                    out _);
                if (count > 0)
                {
                    book.CoverDesign.CompositionSceneJson = sceneJson;
                    book.CoverDesign.Revision++;
                    book.CoverDesign.UpdatedAt = now;
                    historyTargets.Add($"core-cover:{projectId:D}");
                    changed += count;
                    coreChanged = true;
                }
            }
        }
        else if (renderScope != PublicationRenderScope.Interior)
        {
            var cover = await db.PublicationCoverDesigns.SingleOrDefaultAsync(item => item.EditionId == editionId, cancellationToken);
            if (cover is not null)
            {
                var count = ReplaceCoverDesign(cover.CompositionSceneJson, cover.SurfaceScenesJson, replacements, out var sceneJson, out var _, out var surfacesJson);
                if (count > 0)
                {
                    cover.CompositionSceneJson = sceneJson;
                    if (surfacesJson is not null) cover.SurfaceScenesJson = surfacesJson;
                    cover.Revision++;
                    cover.UpdatedAt = now;
                    historyTargets.Add($"release:{editionId:D}:cover:{cover.Id:D}");
                    changed += count;
                    releaseChanged = true;
                }
            }
        }

        if (changed > 0)
        {
            if (coreChanged)
            {
                book.Revision++;
                book.UpdatedAt = now;
            }
            if (releaseChanged && edition is not null)
            {
                edition.Revision++;
                edition.UpdatedAt = now;
            }
            var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
            project.UpdatedAt = now;
        }
        return changed;
    }

    private static (ManuscriptDocument Document, int Count) ReplaceManuscriptImages(
        ManuscriptDocument document,
        IReadOnlyDictionary<Guid, Guid> replacements)
    {
        var count = 0;
        var content = document.Content.Select(block =>
        {
            if (block.Type == ManuscriptBlockType.Figure && block.ImageId is Guid id && replacements.TryGetValue(id, out var replacement))
            {
                count++;
                return block with { ImageId = replacement };
            }
            return block;
        }).ToList();
        return (document with { Content = content }, count);
    }

    private static (CompositionScene Scene, int Count) ReplaceSceneImages(
        CompositionScene scene,
        IReadOnlyDictionary<Guid, Guid> replacements)
    {
        var count = 0;
        var objects = scene.Objects.Select(item =>
        {
            if (item.ImageId is Guid id && replacements.TryGetValue(id, out var replacement))
            {
                count++;
                return item with { ImageId = replacement };
            }
            return item;
        }).ToArray();
        return (scene with { Objects = objects }, count);
    }

    private static int ReplaceCoverDesign(
        string sceneJson,
        string? surfaceJson,
        IReadOnlyDictionary<Guid, Guid> replacements,
        out string updatedSceneJson,
        out int sceneCount,
        out string? updatedSurfaceJson)
    {
        sceneCount = 0;
        updatedSurfaceJson = surfaceJson;
        var scene = string.IsNullOrWhiteSpace(sceneJson) ? null : JsonSerializer.Deserialize<CompositionScene>(sceneJson, JsonOptions);
        if (scene is not null)
        {
            var updated = ReplaceSceneImages(scene, replacements);
            sceneCount += updated.Count;
            updatedSceneJson = JsonSerializer.Serialize(updated.Scene, JsonOptions);
        }
        else
            updatedSceneJson = sceneJson;

        if (!string.IsNullOrWhiteSpace(surfaceJson))
        {
            var surfaces = JsonSerializer.Deserialize<Dictionary<string, string>>(surfaceJson, JsonOptions);
            if (surfaces is not null)
            {
                foreach (var key in surfaces.Keys.ToList())
                {
                    if (string.IsNullOrWhiteSpace(surfaces[key])) continue;
                    var surface = JsonSerializer.Deserialize<CompositionScene>(surfaces[key], JsonOptions);
                    if (surface is null) continue;
                    var updated = ReplaceSceneImages(surface, replacements);
                    sceneCount += updated.Count;
                    surfaces[key] = JsonSerializer.Serialize(updated.Scene, JsonOptions);
                }
                updatedSurfaceJson = JsonSerializer.Serialize(surfaces, JsonOptions);
            }
        }
        return sceneCount;
    }

}

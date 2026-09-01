using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Composition;
using Lorekeeper.Models;
using Lorekeeper.Publish;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

public sealed class ImageGenerationBrief
{
    public string IntendedUse { get; init; } = string.Empty;
    [Description("The required visible story moment and action. Include details grounded by the user or project context, but leave unspecified visual details open to the image model.")]
    public string Scene { get; init; } = string.Empty;
    [Description("Characters and other required subjects. Include every depicted character so each available canonical reference can be supplied.")]
    public string Subjects { get; init; } = string.Empty;
    public string Appearance { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string ExpressionPoseGaze { get; init; } = string.Empty;
    public string Setting { get; init; } = string.Empty;
    public string StyleMediumPalette { get; init; } = string.Empty;
    public string CameraFraming { get; init; } = string.Empty;
    [Description("Placement and visual hierarchy. For page art with editable overlay text, explicitly name the naturally quiet text landing zone and match the protected regions returned by the target read.")]
    public string Composition { get; init; } = string.Empty;
    public string LightingMood { get; init; } = string.Empty;
    [Description("Only meaningful story, continuity, safety, or output constraints. Prefer positive requirements and avoid invented exclusions such as 'and nothing else'.")]
    public string Constraints { get; init; } = string.Empty;
    [Description("Disabled for normal page and cover art. Enable only for intentionally baked-in text.")]
    public bool AllowRenderedText { get; init; }
    public string RenderedText { get; init; } = string.Empty;
}

public sealed class ImageEditBrief
{
    public string IntendedUse { get; init; } = string.Empty;
    [Description("Describe the coherent desired result, not a brittle command such as 'move the character but change nothing else'. For same-aspect up-resolution, require the complete original framing and visible content with credible reconstructed detail and no crop, zoom-out, or surrounding-canvas invention. For intentional outpainting, describe the new framing and surrounding scene direction. Prefer a complete source-driven edit for spatial or compositional changes.")]
    public string Change { get; init; } = string.Empty;
    [Description("Only the identity, story, style, or composition anchors that materially require continuity. Do not require every unmentioned detail to remain exact; let the image model adapt nearby details so the result remains coherent.")]
    public string Preserve { get; init; } = string.Empty;
    public string Composition { get; init; } = string.Empty;
    public string LightingMood { get; init; } = string.Empty;
    public string Constraints { get; init; } = string.Empty;
    public bool AllowRenderedText { get; init; }
    public string RenderedText { get; init; } = string.Empty;
}

public enum ImageEditGuidanceMode
{
    SourceDriven,
    RegionalGuide,
}

public sealed class ImageReferenceUse
{
    [Description("Exact grounded project image id. For every depicted character with an available canonical reference, include one relevant canonical reference before optional variant, setting, prop, or style inputs.")]
    public Guid ImageId { get; init; }
    [Description("The reference's specific job, such as 'canonical identity for Mara' or 'setting architecture'. Name the character when this is an identity reference.")]
    public string Role { get; init; } = string.Empty;
    [Description("Visible identity, design, clothing, palette, prop, setting, or style traits that must carry into the target.")]
    public string TraitsToPreserve { get; init; } = string.Empty;
    [Description("Pose, expression, gaze, action, framing, composition, or other reference traits that the target must replace rather than copy.")]
    public string TraitsThatMustChange { get; init; } = string.Empty;
}

public sealed class ImageGenerationTarget
{
    [Description("Publication edition ID. Supply only for CoverFrame or CoverSurface targets; manuscript and Designed Page targets use project authoring geometry.")]
    public Guid? EditionId { get; init; }
    [Description("ProjectPage, Figure, PageFrame, PageSurface, CoreCoverFrame, CoreCoverSurface, CoverFrame, or CoverSurface for layout-bound generation. Use CoreCoverSurface/CoreCoverFrame for the Core front cover; use CoverSurface/CoverFrame only with an editionId for a release cover.")]
    public string TargetKind { get; init; } = string.Empty;
    [Description("Project ID for ProjectPage, otherwise the stable Figure block, composition object/surface, or cover object/surface ID.")]
    public Guid? TargetId { get; init; }
    [Description("Exact page-composition variant ID. Required for PageFrame and PageSurface targets; omit for Figure and cover targets.")]
    public Guid? VariantId { get; init; }
    [Description("Optional W:H, W/H, or decimal aspect for free-standing work. A regional-guided edit accepts only the source image aspect. Layout-bound targets derive their aspect from Lorekeeper.")]
    public string AspectRatio { get; init; } = string.Empty;
    [Description("Optional explicit WIDTHxHEIGHT provider raster for free-standing generation or editing, or for a layout-bound target when a larger proportional raster is warranted. Both dimensions must satisfy provider constraints. For a regional-guided edit, omit or use auto to derive a source-aspect raster; an explicit raster must preserve the source aspect. Layout-bound targets must preserve the server-owned aspect. Other free-standing work with omitted or auto size uses the configured Core Book page raster; a layout target uses its server-owned moderate raster.")]
    public string Size { get; init; } = string.Empty;
    [Description("Only for free-standing library generation. Layout-bound targets derive every reserved region from Lorekeeper.")]
    public IReadOnlyList<ImageReservedRegion>? ReservedTextRegions { get; init; }
    [Description("Optional positive integer minimum effective DPI for the physical target. Omit size when using minimumDpi. The request is rejected before provider dispatch when the provider cannot meet it; do not confuse this with embedded file metadata. Regional-guide edits cannot request minimumDpi because they are source-aspect-bound.")]
    public int? MinimumDpi { get; init; }
    [Description("Optional canvas-local percentage bounds for generating only a subregion of a verified PageSurface, CoverSurface, or CoreCoverSurface. The bounds become the physical target and all protected regions are transformed into that local coordinate system.")]
    public CompositionBounds? SurfaceBounds { get; init; }
    internal bool MinimumDpiIsSurfaceDefault { get; init; }
    internal bool UseApplicationResolutionPolicy { get; init; }
    internal bool FillTarget { get; init; }
}

public sealed class MinimumDpiUnachievableException : InvalidOperationException
{
    public const string Code = "MINIMUM_DPI_UNACHIEVABLE";

    public MinimumDpiUnachievableException(
        LayoutImageDpiResolution resolution,
        string? requestedRaster,
        string targetKind,
        Guid? targetId,
        Guid? editionId,
        Guid? variantId,
        CompositionBounds? surfaceBounds)
        : base(BuildMessage(resolution, requestedRaster))
    {
        Resolution = resolution;
        RequestedRaster = requestedRaster;
        TargetKind = targetKind;
        TargetId = targetId;
        EditionId = editionId;
        VariantId = variantId;
        SurfaceBounds = surfaceBounds;
        SplitSuggestions = surfaceBounds is null
            ? resolution.PanelSuggestions
            : LayoutImageSizeResolver.MapPanelSuggestions(
                resolution.PanelSuggestions,
                surfaceBounds.XPercent,
                surfaceBounds.YPercent,
                surfaceBounds.WidthPercent,
                surfaceBounds.HeightPercent);
    }

    public string Stage => "pre_dispatch";
    public double RequestedMinimumDpi => Resolution.MinimumDpi;
    public LayoutImageRequiredRaster RequiredRaster => Resolution.RequiredRaster;
    public double MaximumAchievableDpi => Resolution.MaximumAchievableDpi;
    public LayoutImageSize? MaximumRaster => Resolution.MaximumRaster;
    public IReadOnlyList<LayoutImagePanelSuggestion> SplitSuggestions { get; }
    public LayoutImageDpiResolution Resolution { get; }
    public string? RequestedRaster { get; }
    public string TargetKind { get; }
    public Guid? TargetId { get; }
    public Guid? EditionId { get; }
    public Guid? VariantId { get; }
    public CompositionBounds? SurfaceBounds { get; }

    private static string BuildMessage(LayoutImageDpiResolution resolution, string? requestedRaster)
    {
        var requested = string.IsNullOrWhiteSpace(requestedRaster)
            ? $"the required raster is {resolution.RequiredRaster.Size}, but the provider caps at approximately {resolution.MaximumAchievableDpi:0.##} DPI"
            : $"the explicit raster {requestedRaster} resolves to less than the requested minimum";
        return $"Minimum DPI {resolution.MinimumDpi:0.##} cannot be met for the physical target ({resolution.WidthInches:0.####} x {resolution.HeightInches:0.####} inches): {requested}. No image provider request was dispatched.";
    }
}

public sealed class ImageReservedRegion
{
    [Description("Purpose of this region, such as 'body copy' or 'heading'.")]
    public string Label { get; init; } = string.Empty;
    [Description("Left edge as a canvas-local percentage from 0 through 100.")]
    public double XPercent { get; init; }
    [Description("Top edge as a canvas-local percentage from 0 through 100.")]
    public double YPercent { get; init; }
    [Description("Width as a positive canvas-local percentage.")]
    public double WidthPercent { get; init; }
    [Description("Height as a positive canvas-local percentage.")]
    public double HeightPercent { get; init; }
}

public sealed record ImageReferenceManifestEntry(
    int ProviderInputOrder,
    Guid ImageId,
    string FileName,
    string Role,
    string TraitsToPreserve,
    string TraitsThatMustChange);

public sealed record CompiledImagePrompt(
    string Prompt,
    string Size,
    string AspectRatio,
    IReadOnlyList<Guid> ReferenceImageIds,
    IReadOnlyList<ImageReferenceManifestEntry> ReferenceManifest,
    string BriefJson,
    string ReferenceManifestJson,
    string TargetGeometryJson);

public interface IImagePromptComposer
{
    Task<CompiledImagePrompt> CompileGenerationAsync(
        Guid projectId,
        ImageGenerationBrief brief,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? target,
        CancellationToken cancellationToken = default);

    Task<CompiledImagePrompt> CompileEditAsync(
        Guid projectId,
        Guid sourceImageId,
        ImageEditBrief brief,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? target,
        ImageEditGuidanceMode guidanceMode,
        CancellationToken cancellationToken = default);
}

public sealed class ImagePromptComposer(
    ICompositionService compositions,
    IProjectImageService images,
    IProjectImageDefaultRasterResolver defaultRasters,
    IOptions<ProjectImageGenerationOptions> options) : IImagePromptComposer
{
    private const double AspectTolerance = 0.025d;
    private const string PurposefulSpaceInstruction = "Unless the brief explicitly calls for a sparse, minimalist, isolated-study, or open-field composition, concentrate quiet negative space only in explicit text-reservation regions. Everywhere else, make each area contribute to subject, setting, atmosphere, depth, scale, motion, focus, or visual flow without adding clutter. Atmospheric open space is purposeful when it clearly establishes mood or scale; avoid large unmotivated blank areas and do not invent a text landing zone where none was requested.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<CompiledImagePrompt> CompileGenerationAsync(
        Guid projectId,
        ImageGenerationBrief brief,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brief);
        Require(brief.IntendedUse, "intendedUse");
        Require(brief.Scene, "scene");
        ValidateRenderedText(brief.AllowRenderedText, brief.RenderedText);

        var targetResolution = await ResolveTargetAsync(projectId, target, cancellationToken);
        var manifest = await BuildReferenceManifestAsync(projectId, references, firstProviderInputOrder: 1, cancellationToken);
        var builder = new StringBuilder();
        AppendSection(builder, "Intended use", brief.IntendedUse);
        AppendSection(builder, "Scene and background", brief.Scene);
        AppendSection(builder, "Subjects", brief.Subjects);
        AppendSection(builder, "Appearance and continuity anchors", brief.Appearance);
        AppendSection(builder, "Action", brief.Action);
        AppendSection(builder, "Expression, pose, and gaze", brief.ExpressionPoseGaze);
        AppendSection(builder, "Setting and environment", brief.Setting);
        AppendSection(builder, "Style, medium, and palette", brief.StyleMediumPalette);
        AppendSection(builder, "Camera and framing", brief.CameraFraming);
        AppendSection(builder, "Composition", brief.Composition);
        AppendSection(builder, "Purposeful use of space", PurposefulSpaceInstruction);
        AppendSection(builder, "Lighting and mood", brief.LightingMood);
        AppendReferences(builder, manifest);
        AppendSection(builder, "Constraints and exclusions", brief.Constraints);
        AppendSection(
            builder,
            "Creative latitude",
            "Meet every stated story, continuity, composition, and output requirement. Freely compose visual details that are not prescribed by the brief or references so the result feels intentional and coherent. Do not infer that unspecified details are forbidden, and do not add literal exclusions such as 'nothing else'.");
        AppendRenderedTextPolicy(builder, brief.AllowRenderedText, brief.RenderedText);
        AppendTarget(builder, targetResolution, target?.ReservedTextRegions, brief.AllowRenderedText);

        return BuildResult(
            builder,
            targetResolution,
            manifest,
            JsonSerializer.Serialize(brief, JsonOptions));
    }

    public async Task<CompiledImagePrompt> CompileEditAsync(
        Guid projectId,
        Guid sourceImageId,
        ImageEditBrief brief,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? target,
        ImageEditGuidanceMode guidanceMode,
        CancellationToken cancellationToken = default)
    {
        if (sourceImageId == Guid.Empty)
            throw new ArgumentException("sourceImageId is required.", nameof(sourceImageId));
        ArgumentNullException.ThrowIfNull(brief);
        Require(brief.IntendedUse, "intendedUse");
        Require(brief.Change, "change");
        Require(brief.Preserve, "preserve");
        ValidateRenderedText(brief.AllowRenderedText, brief.RenderedText);

        var source = await images.GetAsync(projectId, sourceImageId, cancellationToken)
            ?? throw new InvalidOperationException($"Source image {sourceImageId:N} was not found in this project.");
        if ((references ?? []).Any(reference => reference.ImageId == sourceImageId))
            throw new ArgumentException("The edit source is already provider input image 1 and must not also appear in references.", nameof(references));
        var targetResolution = guidanceMode == ImageEditGuidanceMode.RegionalGuide
            ? await ResolveRegionalGuideTargetAsync(projectId, sourceImageId, target, cancellationToken)
            : await ResolveTargetAsync(projectId, target, cancellationToken);
        var manifest = await BuildReferenceManifestAsync(projectId, references, firstProviderInputOrder: 2, cancellationToken);
        var builder = new StringBuilder();
        AppendSection(builder, "Intended use", brief.IntendedUse);
        AppendSection(builder, "Edit source — provider input image 1", $"{source.FileName} ({source.Id:D}). This is the image to edit.");
        AppendSection(builder, "Desired edited result", brief.Change);
        AppendSection(builder, "Continuity priorities", brief.Preserve);
        AppendSection(builder, "Composition after edit", brief.Composition);
        AppendSection(
            builder,
            "Purposeful use of space after edit",
            guidanceMode == ImageEditGuidanceMode.RegionalGuide
                ? "Preserve the source image's existing balance of occupied and open space outside the guided region. Do not fill, clear, expand, crop, or otherwise redesign surrounding areas unless the requested local change requires a minimal boundary adjustment for coherence."
                : PurposefulSpaceInstruction);
        AppendSection(builder, "Lighting and mood after edit", brief.LightingMood);
        AppendReferences(builder, manifest);
        AppendSection(builder, "Additional constraints and exclusions", brief.Constraints);
        AppendRenderedTextPolicy(builder, brief.AllowRenderedText, brief.RenderedText);
        if (guidanceMode == ImageEditGuidanceMode.RegionalGuide)
            AppendSection(builder, "Regional edit guide", ProjectImageRegionalGuide.PromptInstruction);
        AppendTarget(builder, targetResolution, target?.ReservedTextRegions, brief.AllowRenderedText);
        AppendSection(
            builder,
            "Edit discipline",
            guidanceMode == ImageEditGuidanceMode.RegionalGuide
                ? "Render the requested revision as one coherent complete image using the supplied image as its visual starting point. Preserve the source framing and the explicitly listed identity, story, style, and composition priorities. Concentrate the requested change in the guided region, allow only the minimal nearby lighting, texture, edge, or geometry adaptation needed for coherence, and avoid unrelated changes elsewhere. Keep unrelated major subjects and story facts recognizable without duplicating, deforming, or partially reconstructing them."
                : "Render the requested revision as one coherent complete image using the supplied image as its visual starting point. Preserve the explicitly listed identity, story, style, and composition priorities, while allowing nearby pose, framing, lighting, background, texture, and geometry to adapt naturally when needed. Keep unrelated major subjects and story facts recognizable without duplicating, deforming, or partially reconstructing them.");

        return BuildResult(
            builder,
            targetResolution,
            manifest,
            JsonSerializer.Serialize(brief, JsonOptions));
    }

    private async Task<TargetResolution> ResolveRegionalGuideTargetAsync(
        Guid projectId,
        Guid sourceImageId,
        ImageGenerationTarget? target,
        CancellationToken cancellationToken)
    {
        if (target?.EditionId is not null
            || target?.TargetId is { } targetId && targetId != Guid.Empty
            || !string.IsNullOrWhiteSpace(target?.TargetKind)
            || target?.VariantId is not null)
        {
            throw new ArgumentException(
                "Regional guides cannot be combined with layout-bound targets. Use an unmasked source-driven edit for reframing or layout work.",
                nameof(target));
        }
        if (target?.ReservedTextRegions is { Count: > 0 })
        {
            throw new ArgumentException(
                "Regional guides cannot be combined with reserved text regions. Use an unmasked source-driven edit for composition changes.",
                nameof(target));
        }
        if (target?.MinimumDpi is not null)
        {
            throw new ArgumentException(
                "Regional guides cannot be combined with minimumDpi. Use an unmasked source-driven edit for a physical DPI target.",
                nameof(target));
        }
        if (target?.SurfaceBounds is not null)
        {
            throw new ArgumentException(
                "Regional guides cannot be combined with surfaceBounds. Use an unmasked source-driven edit for layout work.",
                nameof(target));
        }

        var source = await images.GetDataAsync(projectId, sourceImageId, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"Source image {sourceImageId:N} was not found in this project.");
        var sourcePng = ProjectImageBinary.EncodePng(source.Data, source.FileName);
        var sourceAspect = (double)sourcePng.Width / sourcePng.Height;
        if (!string.IsNullOrWhiteSpace(target?.AspectRatio)
            && !LayoutImageSizeResolver.AspectMatches(ParseAspectRatio(target.AspectRatio), sourceAspect))
        {
            throw new ArgumentException(
                $"Regional-guide aspect ratio {target.AspectRatio} must preserve the source image aspect {sourcePng.Width}:{sourcePng.Height}. Use an unmasked edit for reframing.",
                nameof(target));
        }

        var output = ProjectImageRegionalGuide.ResolveOutputSize(sourcePng.Width, sourcePng.Height, target?.Size);
        var aspectLabel = AspectLabel(output.Width, output.Height);
        return new TargetResolution(
            output.Size,
            aspectLabel,
            "Preserve the source framing and aspect. The regional guide identifies only the approximate location of the requested edit; it does not define a hard pixel boundary.",
            JsonSerializer.Serialize(new
            {
                LayoutBound = false,
                RegionalGuide = true,
                SourceRaster = $"{sourcePng.Width}x{sourcePng.Height}",
                Size = output.Size,
                AspectRatio = aspectLabel,
                ReservedTextRegions = Array.Empty<ImageReservedRegion>(),
            }, JsonOptions));
    }

    private static CompiledImagePrompt BuildResult(
        StringBuilder prompt,
        TargetResolution target,
        IReadOnlyList<ImageReferenceManifestEntry> manifest,
        string briefJson) => new(
            prompt.ToString().Trim(),
            target.Size,
            target.AspectRatio,
            manifest.Select(item => item.ImageId).ToList(),
            manifest,
            briefJson,
            JsonSerializer.Serialize(manifest, JsonOptions),
            target.GeometryJson);

    private async Task<IReadOnlyList<ImageReferenceManifestEntry>> BuildReferenceManifestAsync(
        Guid projectId,
        IReadOnlyList<ImageReferenceUse>? references,
        int firstProviderInputOrder,
        CancellationToken cancellationToken)
    {
        var result = new List<ImageReferenceManifestEntry>();
        var seen = new HashSet<Guid>();
        foreach (var reference in references ?? [])
        {
            if (reference.ImageId == Guid.Empty)
                throw new ArgumentException("Every reference must contain an imageId.", nameof(references));
            if (!seen.Add(reference.ImageId))
                throw new ArgumentException($"Reference image {reference.ImageId:N} was supplied more than once.", nameof(references));
            Require(reference.Role, "reference role");
            var image = await images.GetAsync(projectId, reference.ImageId, cancellationToken)
                ?? throw new InvalidOperationException($"Reference image {reference.ImageId:N} was not found in this project.");
            result.Add(new(
                firstProviderInputOrder + result.Count,
                reference.ImageId,
                image.FileName,
                reference.Role.Trim(),
                reference.TraitsToPreserve.Trim(),
                reference.TraitsThatMustChange.Trim()));
        }
        return result;
    }

    private async Task<TargetResolution> ResolveTargetAsync(
        Guid projectId,
        ImageGenerationTarget? target,
        CancellationToken cancellationToken)
    {
        ValidateTargetRequest(target);
        var hasBoundTarget = target?.TargetId is { } targetId && targetId != Guid.Empty
            || !string.IsNullOrWhiteSpace(target?.TargetKind);
        if (hasBoundTarget)
        {
            if (target?.TargetId is not { } boundTargetId || boundTargetId == Guid.Empty
                || string.IsNullOrWhiteSpace(target.TargetKind))
                throw new ArgumentException("Layout-bound targets require targetKind and targetId together.", nameof(target));
            if (!string.IsNullOrWhiteSpace(target.AspectRatio)
                || target.ReservedTextRegions is { Count: > 0 })
                throw new ArgumentException("Layout-bound targets derive aspect ratio and reserved regions from Lorekeeper; omit manual values. An explicit provider-valid Size may select a larger proportional raster when warranted, but it must preserve the server-owned aspect.", nameof(target));
            var coverTarget = target.TargetKind.Trim().StartsWith("cover", StringComparison.OrdinalIgnoreCase);
            LayoutGenerationTargetDescriptor descriptor;
            if (coverTarget)
            {
                if (target.EditionId is not { } boundEditionId || boundEditionId == Guid.Empty)
                    throw new ArgumentException("Cover targets require an editionId.", nameof(target));
                if (target is { MinimumDpi: { } boundedMinimumDpi, SurfaceBounds: { } coverBounds })
                {
                    var surface = await compositions.DescribeGenerationTargetAsync(
                        projectId, boundEditionId, target.TargetKind, boundTargetId, target.VariantId, cancellationToken);
                    RejectUnachievableSurfaceBounds(surface, coverBounds, boundedMinimumDpi, target);
                }
                descriptor = await compositions.DescribeGenerationTargetAsync(
                    projectId, boundEditionId, target.TargetKind, boundTargetId, target.VariantId, cancellationToken, target.SurfaceBounds);
            }
            else
            {
                if (target.EditionId is not null)
                    throw new ArgumentException("Figure and Designed Page targets use project authoring geometry; omit editionId.", nameof(target));
                if (target is { MinimumDpi: { } boundedMinimumDpi, SurfaceBounds: { } authoringBounds })
                {
                    var surface = await compositions.DescribeAuthoringGenerationTargetAsync(
                        projectId, target.TargetKind, boundTargetId, target.VariantId, cancellationToken);
                    RejectUnachievableSurfaceBounds(surface, authoringBounds, boundedMinimumDpi, target);
                }
                descriptor = await compositions.DescribeAuthoringGenerationTargetAsync(
                    projectId, target.TargetKind, boundTargetId, target.VariantId, cancellationToken, target.SurfaceBounds);
            }
            descriptor = target.UseApplicationResolutionPolicy
                ? ApplyApplicationResolutionPolicy(descriptor, target)
                : ApplyMinimumDpi(ApplyLayoutRasterOverride(descriptor, target.Size), target, target.Size);
            if (descriptor.RequestedWidthPixels <= 0 || descriptor.RequestedHeightPixels <= 0)
                throw new ArgumentException("The physical target aspect cannot be represented by one provider-compatible raster. Use a minimum-DPI preflight and its multi-image surface plan, reduce the placement, or choose a compatible frame aspect.", nameof(target));
            var appendix = BuildLayoutTargetAppendix(descriptor, target.FillTarget);
            return new(
                descriptor.RequestedRaster,
                descriptor.AspectRatio,
                appendix,
                JsonSerializer.Serialize(descriptor, JsonOptions),
                descriptor.RequestedMinimumDpi,
                descriptor.PrintUpscalePlan);
        }

        var requestedSize = Clean(target?.Size);
        var requestedAspect = Clean(target?.AspectRatio);
        if (target?.MinimumDpi is { } minimumDpi)
        {
            var hasExplicitSize = requestedSize.Length > 0 && !requestedSize.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (hasExplicitSize)
                throw new ArgumentException("minimumDpi and a concrete size are mutually exclusive; omit size and let Lorekeeper resolve the raster.", nameof(target));

            var intendedAspect = requestedAspect.Length > 0
                ? ParsePositiveAspectRatio(requestedAspect)
                : (double?)null;
            var physicalBasis = await ResolveCoreBookPhysicalBasisAsync(projectId, intendedAspect, cancellationToken);
            var resolution = LayoutImageSizeResolver.ResolveMinimumDpi(
                physicalBasis.WidthInches,
                physicalBasis.HeightInches,
                minimumDpi);
            LayoutPrintUpscalePlan? printUpscalePlan = null;
            if (resolution.Raster is not { } minimumRaster)
            {
                if (resolution.PrintUpscalePlan is not { } resolutionPlan || !options.Value.PrintUpscale)
                    throw CreateMinimumDpiException(resolution, null, target);
                minimumRaster = resolutionPlan.NativeRaster;
                printUpscalePlan = resolutionPlan;
            }
            var minimumAspectLabel = AspectLabel(physicalBasis.WidthInches, physicalBasis.HeightInches);
            return new(
                minimumRaster.Size,
                minimumAspectLabel,
                string.Empty,
                JsonSerializer.Serialize(new
                {
                    LayoutBound = false,
                    CoreBookPhysicalBasis = true,
                    WidthInches = physicalBasis.WidthInches,
                    HeightInches = physicalBasis.HeightInches,
                    Size = minimumRaster.Size,
                    AspectRatio = minimumAspectLabel,
                    ResolvedRasterAspectRatio = AspectLabel(minimumRaster.Width, minimumRaster.Height),
                    RequestedAspectRatio = requestedAspect.Length > 0 ? requestedAspect : null,
                    target.ReservedTextRegions,
                    MinimumDpi = minimumDpi,
                    PrintUpscalePlan = printUpscalePlan,
                }, JsonOptions),
                minimumDpi,
                printUpscalePlan);
        }

        var resolvedSize = await ResolveExplicitSizeAsync(
            projectId,
            requestedSize,
            requestedAspect,
            cancellationToken);
        var (width, height) = ParseAndValidateSize(resolvedSize);
        var actualAspect = (double)width / height;
        if (requestedAspect.Length > 0)
        {
            var expectedAspect = ParseAspectRatio(requestedAspect);
            if (!Approximately(actualAspect, expectedAspect))
                throw new ArgumentException($"size {resolvedSize} conflicts with aspectRatio {requestedAspect}.", nameof(target));
        }

        var aspectLabel = AspectLabel(width, height);
        return new(
            resolvedSize,
            aspectLabel,
            string.Empty,
            JsonSerializer.Serialize(new
            {
                LayoutBound = false,
                Size = resolvedSize,
                AspectRatio = aspectLabel,
                target?.ReservedTextRegions,
            }, JsonOptions));
    }

    private async Task<(double WidthInches, double HeightInches)> ResolveCoreBookPhysicalBasisAsync(
        Guid projectId,
        double? requestedAspect,
        CancellationToken cancellationToken)
    {
        var geometry = await compositions.GetAuthoringGeometryAsync(projectId, cancellationToken);
        var pageWidth = geometry.LeafWidthPoints / 72;
        var pageHeight = geometry.LeafHeightPoints / 72;
        if (requestedAspect is null)
            return (pageWidth, pageHeight);
        var pageAspect = pageWidth / pageHeight;
        return requestedAspect.Value >= pageAspect
            ? (pageWidth, pageWidth / requestedAspect.Value)
            : (pageHeight * requestedAspect.Value, pageHeight);
    }

    private static LayoutGenerationTargetDescriptor ApplyLayoutRasterOverride(
        LayoutGenerationTargetDescriptor descriptor,
        string? requestedSize)
    {
        var normalizedSize = Clean(requestedSize);
        if (normalizedSize.Length == 0 || normalizedSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return descriptor;

        var (width, height) = ParseAndValidateSize(normalizedSize);
        var requestedAspect = (double)width / height;
        var targetAspect = descriptor.WidthInches / descriptor.HeightInches;
        if (!LayoutImageSizeResolver.AspectMatches(requestedAspect, targetAspect))
        {
            throw new ArgumentException(
                $"Layout-bound Size {width}x{height} does not match the physical target aspect {descriptor.AspectRatio}. Keep the server-owned aspect ratio and choose a provider-valid raster.",
                nameof(requestedSize));
        }

        return descriptor with
        {
            RequestedWidthPixels = width,
            RequestedHeightPixels = height,
            RequestedRaster = $"{width}x{height}",
        };
    }

    private LayoutGenerationTargetDescriptor ApplyMinimumDpi(
        LayoutGenerationTargetDescriptor descriptor,
        ImageGenerationTarget target,
        string? requestedSize)
    {
        if (target.MinimumDpi is not { } minimumDpi)
            return descriptor;

        var resolution = LayoutImageSizeResolver.ResolveMinimumDpi(
            descriptor.WidthInches,
            descriptor.HeightInches,
            minimumDpi);
        var normalizedSize = Clean(requestedSize);
        if (normalizedSize.Length > 0 && !normalizedSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var requested = ParseAndValidateSize(normalizedSize);
            var requestedDpi = LayoutImageSizeResolver.EffectiveDpi(
                new LayoutImageSize(requested.Width, requested.Height),
                descriptor.WidthInches,
                descriptor.HeightInches);
            if (requestedDpi + 1e-9 >= minimumDpi)
            {
                return descriptor with
                {
                    EffectiveDpiExpectation = Math.Max(descriptor.EffectiveDpiExpectation, minimumDpi),
                    RequestedMinimumDpi = minimumDpi,
                };
            }

            var requestedPlan = TryCreatePrintUpscalePlan(
                resolution,
                new LayoutImageSize(requested.Width, requested.Height),
                requestedDpi);
            if (requestedPlan is not null)
            {
                return descriptor with
                {
                    EffectiveDpiExpectation = Math.Max(descriptor.EffectiveDpiExpectation, minimumDpi),
                    RequestedMinimumDpi = minimumDpi,
                    PrintUpscalePlan = requestedPlan,
                };
            }
            throw CreateMinimumDpiException(resolution, normalizedSize, target);
        }

        if (resolution.Raster is { } raster)
        {
            return descriptor with
            {
                RequestedWidthPixels = raster.Width,
                RequestedHeightPixels = raster.Height,
                RequestedRaster = raster.Size,
                EffectiveDpiExpectation = Math.Max(descriptor.EffectiveDpiExpectation, minimumDpi),
                RequestedMinimumDpi = minimumDpi,
            };
        }

        if (resolution.PrintUpscalePlan is { } plan && options.Value.PrintUpscale)
        {
            return descriptor with
            {
                RequestedWidthPixels = plan.NativeRaster.Width,
                RequestedHeightPixels = plan.NativeRaster.Height,
                RequestedRaster = plan.NativeRaster.Size,
                EffectiveDpiExpectation = Math.Max(descriptor.EffectiveDpiExpectation, minimumDpi),
                RequestedMinimumDpi = minimumDpi,
                PrintUpscalePlan = plan,
            };
        }

        throw CreateMinimumDpiException(resolution, null, target);
    }

    private LayoutGenerationTargetDescriptor ApplyApplicationResolutionPolicy(
        LayoutGenerationTargetDescriptor descriptor,
        ImageGenerationTarget target)
    {
        var targetDpi = descriptor.EffectiveDpiExpectation;
        var resolution = target.FillTarget
            ? LayoutImageSizeResolver.ResolveFillMinimumDpi(
                descriptor.WidthInches,
                descriptor.HeightInches,
                targetDpi)
            : LayoutImageSizeResolver.ResolveMinimumDpi(
                descriptor.WidthInches,
                descriptor.HeightInches,
                targetDpi);
        if (resolution.Raster is { } raster)
        {
            return descriptor with
            {
                RequestedWidthPixels = raster.Width,
                RequestedHeightPixels = raster.Height,
                RequestedRaster = raster.Size,
                RequestedMinimumDpi = targetDpi,
                CropToFill = target.FillTarget,
            };
        }
        if (resolution.PrintUpscalePlan is { } plan && options.Value.PrintUpscale)
        {
            return descriptor with
            {
                RequestedWidthPixels = plan.NativeRaster.Width,
                RequestedHeightPixels = plan.NativeRaster.Height,
                RequestedRaster = plan.NativeRaster.Size,
                RequestedMinimumDpi = targetDpi,
                PrintUpscalePlan = plan,
                CropToFill = target.FillTarget,
            };
        }

        throw CreateMinimumDpiException(resolution, null, target);
    }

    private LayoutPrintUpscalePlan? TryCreatePrintUpscalePlan(
        LayoutImageDpiResolution resolution,
        LayoutImageSize nativeRaster,
        double nativeEffectiveDpi)
    {
        if (!options.Value.PrintUpscale)
            return null;
        try
        {
            return LayoutImageSizeResolver.CreatePrintUpscalePlan(
                resolution.WidthInches,
                resolution.HeightInches,
                resolution.MinimumDpi,
                nativeRaster,
                nativeEffectiveDpi);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static MinimumDpiUnachievableException CreateMinimumDpiException(
        LayoutImageDpiResolution resolution,
        string? requestedRaster,
        ImageGenerationTarget target) =>
        new(
            resolution,
            requestedRaster,
            target.TargetKind.Trim(),
            target.TargetId,
            target.EditionId,
            target.VariantId,
            target.SurfaceBounds);

    private void RejectUnachievableSurfaceBounds(
        LayoutGenerationTargetDescriptor surface,
        CompositionBounds bounds,
        int minimumDpi,
        ImageGenerationTarget target)
    {
        ValidateSurfaceBounds(bounds);
        var widthInches = surface.WidthInches * bounds.WidthPercent / 100;
        var heightInches = surface.HeightInches * bounds.HeightPercent / 100;
        var resolution = LayoutImageSizeResolver.ResolveMinimumDpi(widthInches, heightInches, minimumDpi);
        if (resolution.MeetsMinimumDpi)
            return;
        if (resolution.PrintUpscalePlan is not null && options.Value.PrintUpscale)
            return;
        throw CreateMinimumDpiException(resolution, null, target);
    }

    private static void ValidateTargetRequest(ImageGenerationTarget? target)
    {
        if (target is null)
            return;
        if (target.MinimumDpi is { } minimumDpi)
            LayoutImageSizeResolver.ValidateMinimumDpi(minimumDpi);
        var layoutBound = target.TargetId is { } targetId
            && targetId != Guid.Empty
            && !string.IsNullOrWhiteSpace(target.TargetKind);
        if (layoutBound && target.MinimumDpi is < 300)
        {
            throw new ArgumentException(
                "Layout-bound Editor and Publish generation requires a minimumDpi of at least 300.",
                nameof(target));
        }
        var concreteSize = Clean(target.Size);
        if (target.MinimumDpi is not null
            && !target.MinimumDpiIsSurfaceDefault
            && concreteSize.Length > 0
            && !concreteSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("minimumDpi and a concrete size are mutually exclusive; omit size and let Lorekeeper resolve the raster.", nameof(target));
        }
        if (target.EditionId is not null
            && target.TargetId is null
            && string.IsNullOrWhiteSpace(target.TargetKind))
        {
            throw new ArgumentException("editionId requires a release cover targetKind and targetId.", nameof(target));
        }
        if (target.VariantId is not null
            && target.TargetId is null
            && string.IsNullOrWhiteSpace(target.TargetKind))
        {
            throw new ArgumentException("variantId requires a layout-bound targetKind and targetId.", nameof(target));
        }
        if (target.SurfaceBounds is not { } bounds)
            return;

        ValidateSurfaceBounds(bounds);
        var normalizedKind = new string((target.TargetKind ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (normalizedKind is not ("pagesurface" or "coversurface" or "corecoversurface"))
        {
            throw new ArgumentException(
                "surfaceBounds requires a PageSurface, CoverSurface, or CoreCoverSurface target.",
                nameof(target));
        }
    }

    private static void ValidateSurfaceBounds(CompositionBounds bounds)
    {
        if (!double.IsFinite(bounds.XPercent)
            || !double.IsFinite(bounds.YPercent)
            || !double.IsFinite(bounds.WidthPercent)
            || !double.IsFinite(bounds.HeightPercent)
            || bounds.XPercent < 0
            || bounds.YPercent < 0
            || bounds.WidthPercent <= 0
            || bounds.HeightPercent <= 0
            || bounds.XPercent + bounds.WidthPercent > 100
            || bounds.YPercent + bounds.HeightPercent > 100)
        {
            throw new ArgumentException("surfaceBounds must use finite positive percentage dimensions inside the 0-100 surface.", nameof(bounds));
        }
    }

    private static string BuildLayoutTargetAppendix(
        LayoutGenerationTargetDescriptor descriptor,
        bool fillTarget)
    {
        var prompt = new StringBuilder();
        prompt.Append("Layout target: ").Append(descriptor.TargetKind)
            .Append("; intended frame aspect ratio ").Append(descriptor.AspectRatio).AppendLine(".");
        prompt.Append(fillTarget
            ? "Compose edge-to-edge so the image can crop-to-fill the complete target. Extend background naturally through any crop margin and keep focal subjects, faces, hands, lettering-safe space, and other essential content within the centered target-aspect window. "
            : "Compose for the complete target while preserving its intended framing. ");
        prompt.Append("Keep important content within usable regions, and do not draw a simulated page border, binding, fold, gutter line, or book mockup.").AppendLine();
        foreach (var region in descriptor.Regions)
        {
            prompt.Append(region.KeepClear ? "Keep clear" : "Layout boundary").Append(": ").Append(region.Label)
                .Append(" (x=").Append(region.Bounds.XPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%, y=").Append(region.Bounds.YPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%, width=").Append(region.Bounds.WidthPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%, height=").Append(region.Bounds.HeightPercent.ToString("0.##", CultureInfo.InvariantCulture)).AppendLine("%).");
        }
        return prompt.ToString().Trim();
    }

    private async Task<string> ResolveExplicitSizeAsync(
        Guid projectId,
        string requestedSize,
        string requestedAspect,
        CancellationToken cancellationToken)
    {
        if (requestedSize.Length > 0 && !requestedSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _ = ParseAndValidateSize(requestedSize);
            return requestedSize.ToLowerInvariant();
        }
        if (requestedAspect.Length > 0)
            return DeriveSize(ParseAspectRatio(requestedAspect));

        return (await defaultRasters.ResolveAsync(projectId, cancellationToken)).Size;
    }

    private static string DeriveSize(double aspect)
    {
        return LayoutImageSizeResolver.ResolveAspect(aspect).Size;
    }

    private static (int Width, int Height) ParseAndValidateSize(string size)
    {
        var parts = size.Trim().ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var height))
        {
            throw new ArgumentException("Image size must be WIDTHxHEIGHT.", nameof(size));
        }
        LayoutImageSizeResolver.Validate(width, height);
        return (width, height);
    }

    private static double ParseAspectRatio(string value)
    {
        var normalized = value.Trim().Replace('/', ':');
        if (normalized.Contains(':'))
        {
            var parts = normalized.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                && width > 0 && height > 0)
            {
                return ValidateAspect(width / height);
            }
        }
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && ratio > 0)
            return ValidateAspect(ratio);
        throw new ArgumentException($"Invalid aspect ratio '{value}'. Use W:H, W/H, or a positive decimal.", nameof(value));
    }

    private static double ParsePositiveAspectRatio(string value)
    {
        var normalized = value.Trim().Replace('/', ':');
        if (normalized.Contains(':'))
        {
            var parts = normalized.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                && double.IsFinite(width)
                && double.IsFinite(height)
                && width > 0
                && height > 0)
            {
                var aspect = width / height;
                if (double.IsFinite(aspect) && aspect > 0)
                    return aspect;
            }
        }
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
            && double.IsFinite(ratio)
            && ratio > 0)
        {
            return ratio;
        }
        throw new ArgumentException($"Invalid aspect ratio '{value}'. Use W:H, W/H, or a positive decimal.", nameof(value));
    }

    private static double ValidateAspect(double aspect)
    {
        if (aspect is < (1d / LayoutImageSizeResolver.MaximumAspectRatio) or > LayoutImageSizeResolver.MaximumAspectRatio)
            throw new ArgumentException("Image aspect ratio must be between 1:3 and 3:1.");
        return aspect;
    }

    private static void AppendReferences(StringBuilder builder, IReadOnlyList<ImageReferenceManifestEntry> manifest)
    {
        if (manifest.Count == 0) return;
        var lines = manifest.Select(item =>
            $"Provider input image {item.ProviderInputOrder}: {item.Role}. Preserve: {Fallback(item.TraitsToPreserve, "only the traits implied by its role")}. Must change: {Fallback(item.TraitsThatMustChange, "nothing unless another instruction says so")}. Image id: {item.ImageId:D}.");
        AppendSection(builder, "Reference image roles (actual provider input order)", string.Join('\n', lines));
    }

    private static void AppendRenderedTextPolicy(StringBuilder builder, bool allowed, string renderedText)
    {
        AppendSection(builder, "Rendered-text policy", allowed
            ? string.IsNullOrWhiteSpace(renderedText)
                ? "Rendered text is permitted only where the composition explicitly calls for it. Keep it legible and do not invent additional words."
                : $"Render only this exact text: {renderedText.Trim()}"
            : "Do not render words, letters, numbers, captions, labels, signatures, watermarks, logos, or story copy. Keep all story text as editable Lorekeeper text outside the raster image.");
    }

    private static void AppendTarget(
        StringBuilder builder,
        TargetResolution target,
        IReadOnlyList<ImageReservedRegion>? reservedRegions,
        bool renderedTextAllowed)
    {
        var targetText = new StringBuilder();
        targetText.Append("Raster size: ").Append(target.Size)
            .Append(". Aspect ratio: ").Append(target.AspectRatio).Append('.');
        if (target.MinimumDpi is { } minimumDpi)
        {
            if (target.PrintUpscalePlan is { } plan)
            {
                targetText.Append(" Final placement DPI: ")
                    .Append(minimumDpi.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("; this request raster ")
                    .Append(plan.NativeRaster.Size)
                    .Append(" is the largest provider-compatible raster for the target, and Lorekeeper upscales the finished image without adding new detail to ")
                    .Append(plan.PrintRaster.Size)
                    .Append(" for the physical print target afterward. Produce the requested raster at full quality without cropping, borders, or simulated resolution.");
            }
            else
            {
                targetText.Append(" Minimum effective DPI: ")
                    .Append(minimumDpi.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("; treat this as a hard output requirement and do not report success unless the returned actual raster meets it.");
            }
        }
        if (!string.IsNullOrWhiteSpace(target.PromptAppendix))
            targetText.Append('\n').Append(target.PromptAppendix);
        foreach (var region in reservedRegions ?? [])
        {
            ValidateRegion(region);
            targetText.Append("\nHard requirement for this generation attempt: reserve ")
                .Append(Fallback(region.Label, "text region"))
                .Append(" at x=").Append(region.XPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%, y=").Append(region.YPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%, width=").Append(region.WidthPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%, height=").Append(region.HeightPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append("%. Make the entire region naturally integrated quiet negative space with simple forms, low detail, low contrast variation, and a stable light or dark value for readable editable type. Keep faces, hands, characters, focal objects, important action, sharp edges, high-frequency texture, and strong value transitions outside it. Do not draw a placeholder rectangle, frame, sign, or caption panel.");
            targetText.Append(renderedTextAllowed
                ? " When the rendered-text policy supplies exact text, place only that text within the intended reserved region and keep the underlying composition suitable for later text removal."
                : " Do not draw text inside it.");
            targetText.Append(" This region is binding for this image attempt even though the page-layout assistant may revise the editable text placement after inspecting the result.");
        }
        AppendSection(builder, "Output target and protected regions", targetText.ToString());
    }

    private static void ValidateRegion(ImageReservedRegion region)
    {
        if (region.XPercent < 0 || region.YPercent < 0
            || region.WidthPercent <= 0 || region.HeightPercent <= 0
            || region.XPercent + region.WidthPercent > 100
            || region.YPercent + region.HeightPercent > 100)
        {
            throw new ArgumentException("Reserved text regions must use positive 0-100 percentage bounds inside the canvas.");
        }
    }

    private static void AppendSection(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.Append("\n\n");
        builder.Append("## ").Append(label).Append('\n').Append(value.Trim());
    }

    private static void ValidateRenderedText(bool allowed, string renderedText)
    {
        if (!allowed && !string.IsNullOrWhiteSpace(renderedText))
            throw new ArgumentException("renderedText requires allowRenderedText=true.");
    }

    private static void Require(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{fieldName} is required.", fieldName);
    }

    private static bool Approximately(double left, double right) =>
        Math.Abs(left - right) / Math.Max(Math.Abs(right), double.Epsilon) <= AspectTolerance;

    private static string AspectLabel(int width, int height)
    {
        var divisor = GreatestCommonDivisor(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    private static string AspectLabel(double width, double height)
    {
        var scaledWidth = (long)Math.Round(width * 1_000_000);
        var scaledHeight = (long)Math.Round(height * 1_000_000);
        var divisor = GreatestCommonDivisor(scaledWidth, scaledHeight);
        return $"{scaledWidth / divisor}:{scaledHeight / divisor}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Max(Math.Abs(left), 1);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static string Fallback(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record TargetResolution(
        string Size,
        string AspectRatio,
        string PromptAppendix,
        string GeometryJson,
        double? MinimumDpi = null,
        LayoutPrintUpscalePlan? PrintUpscalePlan = null);
}

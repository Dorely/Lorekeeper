using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lorekeeper.Chapters;
using Lorekeeper.ChapterVisuals;
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
    [Description("Placement and visual hierarchy. For page art with editable overlay text, explicitly name the naturally quiet text landing zone and match it to target.reservedTextRegions.")]
    public string Composition { get; init; } = string.Empty;
    public string LightingMood { get; init; } = string.Empty;
    [Description("Only meaningful story, continuity, safety, or output constraints. Prefer positive requirements and avoid invented exclusions such as 'and nothing else'.")]
    public string Constraints { get; init; } = string.Empty;
    [Description("Disabled for normal page art. Enable only for intentionally baked-in text or the temporary text-bearing image in a two-pass PicturePage fallback.")]
    public bool AllowRenderedText { get; init; }
    public string RenderedText { get; init; } = string.Empty;
}

public sealed class ImageEditBrief
{
    public string IntendedUse { get; init; } = string.Empty;
    [Description("Describe the coherent desired result, not a brittle command such as 'move the character but change nothing else'. Prefer regeneration for spatial or compositional changes.")]
    public string Change { get; init; } = string.Empty;
    [Description("Only the identity, story, style, or composition anchors that materially require continuity. Do not require every unmentioned pixel or secondary detail to remain exact.")]
    public string Preserve { get; init; } = string.Empty;
    public string Composition { get; init; } = string.Empty;
    public string LightingMood { get; init; } = string.Empty;
    public string Constraints { get; init; } = string.Empty;
    public bool AllowRenderedText { get; init; }
    public string RenderedText { get; init; } = string.Empty;
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
    public Guid? ChapterId { get; init; }
    public Guid? PictureImageElementId { get; init; }
    public string PageSlot { get; init; } = string.Empty;
    public string AspectRatio { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    [Description("Canvas-local percentage rectangles that the illustration must preserve as natural, quiet negative space for editable overlaid text. For a full-page target, use the corresponding page bounds for the text boxes; for an image-slot target, translate through the slot geometry.")]
    public IReadOnlyList<ImageReservedRegion>? ReservedTextRegions { get; init; }
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
        CancellationToken cancellationToken = default);
}

public sealed class ImagePromptComposer(
    IChapterService chapters,
    IChapterVisualService chapterVisuals,
    IProjectImageService images,
    IPageGeometryService pageGeometry,
    IOptions<ProjectImageGenerationOptions> options) : IImagePromptComposer
{
    private const int SizeMultiple = 16;
    private const int MinImagePixels = 655_360;
    private const int MaxImagePixels = 8_294_400;
    private const int MaxImageEdge = 3840;
    private const double MaxImageAspectRatio = 3d;
    private const double AspectTolerance = 0.025d;
    private const int TargetAreaPixels = 1_572_864;

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
        var targetResolution = await ResolveTargetAsync(projectId, target, cancellationToken);
        var manifest = await BuildReferenceManifestAsync(projectId, references, firstProviderInputOrder: 2, cancellationToken);
        var builder = new StringBuilder();
        AppendSection(builder, "Intended use", brief.IntendedUse);
        AppendSection(builder, "Edit source — provider input image 1", $"{source.FileName} ({source.Id:D}). This is the image to edit.");
        AppendSection(builder, "Desired edited result", brief.Change);
        AppendSection(builder, "Continuity priorities", brief.Preserve);
        AppendSection(builder, "Composition after edit", brief.Composition);
        AppendSection(builder, "Lighting and mood after edit", brief.LightingMood);
        AppendReferences(builder, manifest);
        AppendSection(builder, "Additional constraints and exclusions", brief.Constraints);
        AppendRenderedTextPolicy(builder, brief.AllowRenderedText, brief.RenderedText);
        AppendTarget(builder, targetResolution, target?.ReservedTextRegions, brief.AllowRenderedText);
        AppendSection(
            builder,
            "Edit discipline",
            "Make the requested revision as one coherent image. Preserve the explicitly listed continuity priorities, but do not freeze every unmentioned pixel or secondary detail. Allow nearby pose, framing, lighting, background, texture, and geometry to adapt naturally when needed to integrate the edit. Keep unrelated major subjects and story facts recognizable without duplicating, deforming, or partially reconstructing them.");

        return BuildResult(
            builder,
            targetResolution,
            manifest,
            JsonSerializer.Serialize(brief, JsonOptions));
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
        if (target?.PictureImageElementId is { } elementId && elementId != Guid.Empty
            && (target.ChapterId is null || target.ChapterId == Guid.Empty))
        {
            throw new ArgumentException("pictureImageElementId requires chapterId.", nameof(target));
        }

        if (target?.ChapterId is { } targetChapterId && targetChapterId != Guid.Empty)
        {
            var chapter = await chapters.GetAsync(targetChapterId, cancellationToken);
            if (chapter is null || chapter.ProjectId != projectId)
                throw new InvalidOperationException($"Target chapter {targetChapterId:N} was not found in this project.");
            var state = await chapterVisuals.GetAsync(targetChapterId, cancellationToken)
                ?? throw new InvalidOperationException($"Target chapter {targetChapterId:N} has no visual layout.");
            var geometry = await pageGeometry.GetAsync(projectId, state.PageLayoutKind, cancellationToken);
            if (!PicturePageImageGenerationGuidance.TryResolveTarget(
                    state,
                    geometry,
                    target.PictureImageElementId,
                    out var pageTarget,
                    out var error))
            {
                throw new InvalidOperationException(error);
            }

            var recommended = pageTarget!;
            var resolvedTargetSize = ResolveTargetSize(
                target.Size,
                target.AspectRatio,
                recommended.RecommendedSize,
                recommended.AspectRatio);
            var appendix = PicturePageImageGenerationGuidance.BuildPromptAppendix(recommended, state.PageLayout.TextElements);
            return new(
                resolvedTargetSize,
                recommended.AspectRatio,
                appendix.Trim(),
                JsonSerializer.Serialize(new
                {
                    target.ChapterId,
                    target.PictureImageElementId,
                    target.PageSlot,
                    PageTarget = recommended,
                    ReservedTextRegions = target.ReservedTextRegions,
                }, JsonOptions));
        }

        var requestedSize = Clean(target?.Size);
        var requestedAspect = Clean(target?.AspectRatio);
        var resolvedSize = ResolveExplicitSize(requestedSize, requestedAspect);
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
                target?.PageSlot,
                Size = resolvedSize,
                AspectRatio = aspectLabel,
                target?.ReservedTextRegions,
            }, JsonOptions));
    }

    private string ResolveExplicitSize(string requestedSize, string requestedAspect)
    {
        if (requestedSize.Length > 0 && !requestedSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _ = ParseAndValidateSize(requestedSize);
            return requestedSize.ToLowerInvariant();
        }
        if (requestedAspect.Length > 0)
            return DeriveSize(ParseAspectRatio(requestedAspect));

        var configured = Clean(options.Value.DefaultSize);
        if (configured.Length > 0 && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _ = ParseAndValidateSize(configured);
            return configured.ToLowerInvariant();
        }
        return "1024x1024";
    }

    private static string ResolveTargetSize(
        string requestedSize,
        string requestedAspect,
        string targetSize,
        string targetAspect)
    {
        var targetGeometryAspect = ParseAspectRatio(targetAspect);
        var (targetWidth, targetHeight) = ParseAndValidateSize(targetSize);
        var targetRasterAspect = (double)targetWidth / targetHeight;
        if (!Approximately(targetRasterAspect, targetGeometryAspect))
        {
            throw new InvalidOperationException(
                $"Target geometry {targetAspect} cannot be represented by the derived gpt-image-2 raster {targetSize} without changing its aspect ratio.");
        }

        var resolvedSize = targetSize;
        if (!string.IsNullOrWhiteSpace(requestedSize)
            && !requestedSize.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var (width, height) = ParseAndValidateSize(requestedSize);
            if (!Approximately((double)width / height, targetGeometryAspect))
                throw new ArgumentException($"Explicit size {requestedSize} conflicts with target geometry {targetSize}.");
            resolvedSize = requestedSize.Trim().ToLowerInvariant();
        }
        if (!string.IsNullOrWhiteSpace(requestedAspect)
            && !Approximately(ParseAspectRatio(requestedAspect), targetGeometryAspect))
        {
            throw new ArgumentException($"Explicit aspectRatio {requestedAspect} conflicts with target geometry {targetAspect}.");
        }
        return resolvedSize;
    }

    private static string DeriveSize(double aspect)
    {
        if (Approximately(aspect, 1d)) return "1024x1024";
        if (Approximately(aspect, 2d / 3d)) return "1024x1536";
        if (Approximately(aspect, 3d / 2d)) return "1536x1024";

        var height = Math.Sqrt(TargetAreaPixels / aspect);
        var width = height * aspect;
        var roundedWidth = Math.Clamp(RoundToMultiple(width), SizeMultiple, MaxImageEdge);
        var roundedHeight = Math.Clamp(RoundToMultiple(height), SizeMultiple, MaxImageEdge);
        var pixels = roundedWidth * roundedHeight;
        if (pixels < MinImagePixels || pixels > MaxImagePixels)
            throw new ArgumentException("Could not derive a valid gpt-image-2 raster size for the requested aspect ratio.");
        return $"{roundedWidth}x{roundedHeight}";
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
        if (width % SizeMultiple != 0 || height % SizeMultiple != 0)
            throw new ArgumentException($"Image width and height must be divisible by {SizeMultiple}.", nameof(size));
        if (width > MaxImageEdge || height > MaxImageEdge)
            throw new ArgumentException($"Image edges cannot exceed {MaxImageEdge} pixels.", nameof(size));
        var pixels = (long)width * height;
        if (pixels is < MinImagePixels or > MaxImagePixels)
            throw new ArgumentException($"Image size must contain {MinImagePixels:N0}-{MaxImagePixels:N0} pixels.", nameof(size));
        var aspect = (double)width / height;
        if (aspect is < (1d / MaxImageAspectRatio) or > MaxImageAspectRatio)
            throw new ArgumentException("Image aspect ratio must be between 1:3 and 3:1.", nameof(size));
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

    private static double ValidateAspect(double aspect)
    {
        if (aspect is < (1d / MaxImageAspectRatio) or > MaxImageAspectRatio)
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

    private static int RoundToMultiple(double value) =>
        Math.Max(SizeMultiple, (int)Math.Round(value / SizeMultiple) * SizeMultiple);

    private static string AspectLabel(int width, int height)
    {
        var divisor = GreatestCommonDivisor(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static string Fallback(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private sealed record TargetResolution(
        string Size,
        string AspectRatio,
        string PromptAppendix,
        string GeometryJson);
}

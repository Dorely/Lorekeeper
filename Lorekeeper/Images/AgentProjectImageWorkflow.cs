using Lorekeeper.Models;
using Lorekeeper.Composition;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

public interface IAgentProjectImageWorkflow
{
    Task<AgentProjectImageResult> GenerateAsync(
        Guid projectId,
        ImageGenerationBrief brief,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string label,
        Action<Guid>? onJobCreated = null,
        CancellationToken cancellationToken = default,
        int? defaultMinimumDpi = null);

    Task<AgentProjectImageResult> EditAsync(
        Guid projectId,
        Guid sourceImageId,
        ImageEditBrief brief,
        ProjectImageMaskShapeRequest? regionalGuide,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string label,
        Action<Guid>? onJobCreated = null,
        CancellationToken cancellationToken = default,
        int? defaultMinimumDpi = null);

    Task<AgentProjectImageResult?> ReadAsync(
        Guid projectId,
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<AgentProjectImageResult?> WaitAsync(
        Guid projectId,
        Guid jobId,
        Action<Guid>? onJobTracked = null,
        CancellationToken cancellationToken = default);

    Task CancelAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
}

public sealed record AgentProjectImageResult(
    Guid JobId,
    string Status,
    bool IsTerminal,
    bool Succeeded,
    string RequestedRaster,
    string TargetAspect,
    bool LayoutBound,
    IReadOnlyList<AgentProjectImageOutput> Outputs,
    IReadOnlyList<ProjectImageOutputErrorView> Diagnostics,
    string Summary,
    double? RequestedMinimumDpi = null,
    bool? MinimumDpiMet = null,
    IReadOnlyList<string>? WarningCodes = null)
{
    public IReadOnlyList<ProjectImageView> Images => Outputs.Select(output => output.Image).ToList();
}

public sealed record AgentProjectImageOutput(
    ProjectImageView Image,
    int Width,
    int Height,
    string ActualRaster,
    bool RasterMatched,
    bool AspectMatched,
    double? EffectiveDpi,
    double? RequestedMinimumDpi = null,
    bool? MinimumDpiMet = null,
    IReadOnlyList<string>? WarningCodes = null,
    Guid? PrintImageId = null,
    string? PrintRaster = null,
    double? PrintEffectiveDpi = null);

public sealed class AgentProjectImageWorkflow(
    IImagePromptComposer prompts,
    IProjectImageJobService jobs,
    IProjectImageGenerationRuntime runtime,
    IProjectImageService images,
    IOptions<ProjectImageGenerationOptions> options) : IAgentProjectImageWorkflow
{
    public async Task<AgentProjectImageResult> GenerateAsync(
        Guid projectId,
        ImageGenerationBrief brief,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string label,
        Action<Guid>? onJobCreated = null,
        CancellationToken cancellationToken = default,
        int? defaultMinimumDpi = null)
    {
        var effectiveTarget = WithDefaultMinimumDpi(geometryGuidance, defaultMinimumDpi);
        var compiled = await prompts.CompileGenerationAsync(
            projectId,
            brief,
            references?.ToArray(),
            effectiveTarget,
            cancellationToken);
        var job = await jobs.CreateGenerateJobAsync(
            projectId,
            new ProjectImageGenerateJobRequest(
                compiled.Prompt,
                compiled.Size,
                CleanOr(quality, options.Value.DefaultQuality),
                CleanOr(outputFormat, options.Value.DefaultOutputFormat),
                outputCompression,
                altText?.Trim() ?? string.Empty,
                1,
                compiled.ReferenceImageIds,
                label,
                EntityTargets: null,
                compiled.BriefJson,
                compiled.ReferenceManifestJson,
                compiled.TargetGeometryJson),
            cancellationToken);
        return await RunAsync(projectId, job.Id, onJobCreated, cancellationToken);
    }

    public async Task<AgentProjectImageResult> EditAsync(
        Guid projectId,
        Guid sourceImageId,
        ImageEditBrief brief,
        ProjectImageMaskShapeRequest? regionalGuide,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string label,
        Action<Guid>? onJobCreated = null,
        CancellationToken cancellationToken = default,
        int? defaultMinimumDpi = null)
    {
        var effectiveTarget = WithDefaultMinimumDpi(
            geometryGuidance,
            regionalGuide is null ? defaultMinimumDpi : null);
        var compiled = await prompts.CompileEditAsync(
            projectId,
            sourceImageId,
            brief,
            references?.ToArray(),
            effectiveTarget,
            regionalGuide is null ? ImageEditGuidanceMode.SourceDriven : ImageEditGuidanceMode.RegionalGuide,
            cancellationToken);
        var job = await jobs.CreateEditJobAsync(
            projectId,
            new ProjectImageEditJobRequest(
                sourceImageId,
                compiled.Prompt,
                compiled.Size,
                CleanOr(quality, options.Value.DefaultQuality),
                CleanOr(outputFormat, options.Value.DefaultOutputFormat),
                outputCompression,
                altText?.Trim() ?? string.Empty,
                1,
                MaskPngDataUrl: null,
                ReferenceImageIds: compiled.ReferenceImageIds,
                Label: label,
                RegionalGuide: regionalGuide,
                EntityTargets: null,
                InheritSourceEntityTargets: false,
                compiled.BriefJson,
                compiled.ReferenceManifestJson,
                compiled.TargetGeometryJson),
            cancellationToken);
        return await RunAsync(projectId, job.Id, onJobCreated, cancellationToken);
    }

    public async Task<AgentProjectImageResult?> ReadAsync(
        Guid projectId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetJobAsync(projectId, jobId, cancellationToken);
        return job is null ? null : await ToResultAsync(projectId, job, timedOut: false, cancellationToken);
    }

    public async Task<AgentProjectImageResult?> WaitAsync(
        Guid projectId,
        Guid jobId,
        Action<Guid>? onJobTracked = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await jobs.GetJobAsync(projectId, jobId, cancellationToken);
        if (existing is null)
            return null;
        if (IsTerminal(existing.Status))
            return await ToResultAsync(projectId, existing, timedOut: false, cancellationToken);
        return await RunAsync(projectId, jobId, onJobTracked, cancellationToken);
    }

    public Task CancelAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default) =>
        runtime.CancelJobAsync(projectId, jobId, cancellationToken);

    private async Task<AgentProjectImageResult> RunAsync(
        Guid projectId,
        Guid jobId,
        Action<Guid>? onJobCreated,
        CancellationToken cancellationToken)
    {
        onJobCreated?.Invoke(jobId);
        try
        {
            await runtime.EnqueueProjectAsync(projectId, cancellationToken);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.AgentJobWaitTimeoutSeconds, 1, 3600));
            var completed = await runtime.WaitForJobCompletionAsync(jobId, timeout, cancellationToken);
            if (!completed)
                await runtime.CancelJobAsync(projectId, jobId, CancellationToken.None);
            var job = await jobs.GetJobAsync(projectId, jobId, CancellationToken.None)
                ?? throw new InvalidOperationException($"Image job {jobId:N} was not found after it was queued.");
            return await ToResultAsync(projectId, job, timedOut: !completed, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await runtime.CancelJobAsync(projectId, jobId, CancellationToken.None);
            throw;
        }
    }

    private async Task<AgentProjectImageResult> ToResultAsync(
        Guid projectId,
        ProjectImageJobView job,
        bool timedOut,
        CancellationToken cancellationToken)
    {
        var geometry = ReadGeometry(job);
        var hasRequestedRaster = LayoutImageSizeResolver.TryParse(job.Size, out var requested);
        if (geometry is not null && !hasRequestedRaster)
            throw new InvalidDataException("Layout-bound image jobs require an explicit requested raster.");
        var providerSucceeded = !timedOut && job.Status is ProjectImageGenerationJobStatus.Succeeded
            or ProjectImageGenerationJobStatus.CompletedWithErrors;
        var printUpscale = geometry is { HasPrintUpscalePlan: true }
            ? (PrintWidth: geometry.PrintWidth!.Value, PrintHeight: geometry.PrintHeight!.Value, TargetDpi: geometry.PrintTargetDpi!.Value)
            : ((int PrintWidth, int PrintHeight, double TargetDpi)?)null;
        var outputImages = new List<AgentProjectImageOutput>();
        var warningCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var imageId in job.OutputImageIds)
        {
            if (await images.GetAsync(projectId, imageId, cancellationToken) is { } image)
            {
                var data = await images.GetDataAsync(projectId, imageId, cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException($"Completed project image {imageId:N} has no readable data.");
                var normalized = ProjectImageBinary.Normalize(data.Data, data.ContentType, data.FileName, int.MaxValue);
                var rasterMatched = !hasRequestedRaster
                    || normalized.Width == requested.Width && normalized.Height == requested.Height;
                var aspectMatched = geometry is { WidthInches: > 0, HeightInches: > 0 }
                    ? LayoutImageSizeResolver.AspectMatches(
                        (double)normalized.Width / normalized.Height,
                        geometry.WidthInches / geometry.HeightInches)
                    : !hasRequestedRaster
                        || LayoutImageSizeResolver.AspectMatches(
                            (double)normalized.Width / normalized.Height,
                            (double)requested.Width / requested.Height);
                double? effectiveDpi = geometry is { WidthInches: > 0, HeightInches: > 0 }
                    ? Math.Min(normalized.Width / geometry.WidthInches, normalized.Height / geometry.HeightInches)
                    : null;
                var outputWarnings = new List<string>();
                if (!rasterMatched)
                    outputWarnings.Add("PROVIDER_IMAGE_RASTER_MISMATCH");
                if (!aspectMatched)
                    outputWarnings.Add("LAYOUT_IMAGE_ASPECT_MISMATCH");
                var outputMinimumDpiMet = geometry?.MinimumDpi is not { } minimumDpi
                    ? (bool?)null
                    : printUpscale is not null
                        ? true
                        : effectiveDpi is { } actualDpi && actualDpi + 1e-9 >= minimumDpi;
                if (printUpscale is not null)
                    outputWarnings.Add("PRINT_DPI_UPSCALED");
                else if (outputMinimumDpiMet == false)
                    outputWarnings.Add("MINIMUM_DPI_NOT_MET");
                foreach (var warningCode in outputWarnings)
                    warningCodes.Add(warningCode);
                outputImages.Add(new AgentProjectImageOutput(
                    image,
                    normalized.Width,
                    normalized.Height,
                    $"{normalized.Width}x{normalized.Height}",
                    rasterMatched,
                    aspectMatched,
                    effectiveDpi,
                    geometry?.MinimumDpi,
                    outputMinimumDpiMet,
                    outputWarnings));
            }
        }

        if (printUpscale is { } plan && providerSucceeded
            && outputImages.Count > 0
            && geometry is { WidthInches: > 0, HeightInches: > 0 })
        {
            var printRequest = new ProjectImagePrintUpscaleRequest(
                plan.PrintWidth,
                plan.PrintHeight,
                geometry.WidthInches,
                geometry.HeightInches,
                plan.TargetDpi);
            for (var index = 0; index < outputImages.Count; index++)
            {
                var existingOutput = outputImages[index];
                var printResult = await images.EnsurePrintUpscaleAsync(projectId, existingOutput.Image.Id, printRequest, cancellationToken);
                var printView = printResult.Image;
                outputImages[index] = existingOutput with
                {
                    PrintImageId = printView.Id,
                    PrintRaster = $"{plan.PrintWidth}x{plan.PrintHeight}",
                    PrintEffectiveDpi = Math.Min(plan.PrintWidth / geometry.WidthInches, plan.PrintHeight / geometry.HeightInches),
                };
            }
        }

        var minimumDpiMet = geometry?.MinimumDpi is null || outputImages.Count == 0
            ? (bool?)null
            : outputImages.All(output => output.MinimumDpiMet == true);
        var succeeded = providerSucceeded && minimumDpiMet != false;
        var status = timedOut
            ? "timed_out"
            : providerSucceeded && minimumDpiMet == false
                ? "minimum_dpi_not_met"
                : StatusName(job.Status);
        var summary = timedOut
            ? "Image generation exceeded the configured lifetime and was cancelled; no image was placed."
            : providerSucceeded && minimumDpiMet == false
                ? "The provider output was retained as an unattached image, but it did not meet the requested minimum DPI and is not publication-compliant."
            : succeeded && printUpscale is { } appliedPlan
                ? $"{outputImages.Count} unattached project image(s) completed and were upscaled to {appliedPlan.PrintWidth}x{appliedPlan.PrintHeight} for the {appliedPlan.TargetDpi:0} DPI physical print target from the provider's largest compatible raster. Place the print-upscaled image ID, not the native one."
            : succeeded
                ? $"{outputImages.Count} unattached project image(s) completed. Inspect an image, then place its ID with a separate tool."
                : job.Status == ProjectImageGenerationJobStatus.Cancelled
                    ? "Image generation was cancelled; no image was placed."
                    : string.IsNullOrWhiteSpace(job.Error)
                        ? "Image generation failed; no image was placed."
                        : job.Error;
        return new AgentProjectImageResult(
            job.Id,
            status,
            IsTerminal(job.Status) || timedOut,
            succeeded,
            job.Size,
            geometry?.AspectRatio
                ?? (hasRequestedRaster
                    ? AspectLabel(requested.Width, requested.Height)
                    : outputImages.FirstOrDefault() is { } output
                        ? AspectLabel(output.Width, output.Height)
                        : "unknown"),
            geometry?.LayoutBound ?? false,
            outputImages,
            job.OutputErrors,
            summary,
            geometry?.MinimumDpi,
            minimumDpiMet,
            warningCodes.ToList());
    }

    private static bool IsTerminal(ProjectImageGenerationJobStatus status) => status is
        ProjectImageGenerationJobStatus.Succeeded or
        ProjectImageGenerationJobStatus.CompletedWithErrors or
        ProjectImageGenerationJobStatus.Failed or
        ProjectImageGenerationJobStatus.Cancelled;

    private static string StatusName(ProjectImageGenerationJobStatus status) => status switch
    {
        ProjectImageGenerationJobStatus.CompletedWithErrors => "completed_with_errors",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string CleanOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ImageGenerationTarget? WithDefaultMinimumDpi(
        ImageGenerationTarget? target,
        int? defaultMinimumDpi)
    {
        if (defaultMinimumDpi is not { } minimumDpi
            || target?.MinimumDpi is not null)
        {
            return target;
        }

        LayoutImageSizeResolver.ValidateMinimumDpi(minimumDpi);
        if (target?.TargetId is not Guid targetId
            || targetId == Guid.Empty
            || string.IsNullOrWhiteSpace(target.TargetKind))
            return target;

        return new ImageGenerationTarget
        {
            EditionId = target.EditionId,
            TargetKind = target.TargetKind,
            TargetId = target.TargetId,
            VariantId = target.VariantId,
            AspectRatio = target.AspectRatio,
            Size = target.Size,
            ReservedTextRegions = target.ReservedTextRegions,
            MinimumDpi = minimumDpi,
            SurfaceBounds = target.SurfaceBounds,
            MinimumDpiIsSurfaceDefault = true,
        };
    }

    private static LayoutGeometry? ReadGeometry(ProjectImageJobView job)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(job.TargetGeometryJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("widthInches", out var width)
                || !width.TryGetDouble(out var widthInches)
                || !root.TryGetProperty("heightInches", out var height)
                || !height.TryGetDouble(out var heightInches))
            {
                return null;
            }

            var aspect = root.TryGetProperty("aspectRatio", out var aspectValue)
                && aspectValue.ValueKind == System.Text.Json.JsonValueKind.String
                    ? aspectValue.GetString() ?? string.Empty
                    : string.Empty;
            var layoutBound = root.TryGetProperty("targetKind", out var targetKind)
                    && targetKind.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(targetKind.GetString())
                || root.TryGetProperty("layoutBound", out var layoutValue)
                    && layoutValue.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                    && layoutValue.GetBoolean();
            var minimumDpi = TryReadPositiveDouble(root, "minimumDpi")
                ?? TryReadPositiveDouble(root, "requestedMinimumDpi");
            int? printWidth = null;
            int? printHeight = null;
            double? printTargetDpi = null;
            if (root.TryGetProperty("printUpscalePlan", out var plan)
                && plan.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                printWidth = TryReadNestedPositiveInt(plan, "printRaster", "width");
                printHeight = TryReadNestedPositiveInt(plan, "printRaster", "height");
                printTargetDpi = TryReadPositiveDouble(plan, "targetDpi");
                if (printWidth is null || printHeight is null || printTargetDpi is null)
                {
                    printWidth = null;
                    printHeight = null;
                    printTargetDpi = null;
                }
            }
            return new LayoutGeometry(
                widthInches,
                heightInches,
                aspect,
                layoutBound,
                minimumDpi,
                printWidth,
                printHeight,
                printTargetDpi);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static double? TryReadPositiveDouble(
        System.Text.Json.JsonElement root,
        string propertyName) => root.TryGetProperty(propertyName, out var value)
            && value.TryGetDouble(out var parsed)
            && parsed > 0
                ? parsed
                : null;

    private static int? TryReadNestedPositiveInt(
        System.Text.Json.JsonElement root,
        string objectName,
        string propertyName)
    {
        if (!root.TryGetProperty(objectName, out var nested)
            || nested.ValueKind != System.Text.Json.JsonValueKind.Object
            || !nested.TryGetProperty(propertyName, out var value)
            || !value.TryGetInt32(out var parsed)
            || parsed <= 0)
        {
            return null;
        }
        return parsed;
    }

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

    private sealed record LayoutGeometry(
        double WidthInches,
        double HeightInches,
        string AspectRatio,
        bool LayoutBound,
        double? MinimumDpi,
        int? PrintWidth = null,
        int? PrintHeight = null,
        double? PrintTargetDpi = null)
    {
        public bool HasPrintUpscalePlan => PrintWidth is not null
            && PrintHeight is not null
            && PrintTargetDpi is not null;
    }
}

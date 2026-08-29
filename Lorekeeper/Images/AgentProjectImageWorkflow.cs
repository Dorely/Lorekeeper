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
        CancellationToken cancellationToken = default);

    Task<AgentProjectImageResult> EditAsync(
        Guid projectId,
        Guid sourceImageId,
        ImageEditBrief brief,
        Guid? maskId,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string label,
        Action<Guid>? onJobCreated = null,
        CancellationToken cancellationToken = default);

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
    string Summary)
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
    double? EffectiveDpi);

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
        CancellationToken cancellationToken = default)
    {
        var compiled = await prompts.CompileGenerationAsync(
            projectId,
            brief,
            references?.ToArray(),
            geometryGuidance,
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
        Guid? maskId,
        IReadOnlyList<ImageReferenceUse>? references,
        ImageGenerationTarget? geometryGuidance,
        string? altText,
        string? quality,
        string? outputFormat,
        int? outputCompression,
        string label,
        Action<Guid>? onJobCreated = null,
        CancellationToken cancellationToken = default)
    {
        var compiled = await prompts.CompileEditAsync(
            projectId,
            sourceImageId,
            brief,
            references?.ToArray(),
            geometryGuidance,
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
                ExistingMaskId: maskId,
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
        var outputImages = new List<AgentProjectImageOutput>();
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
                outputImages.Add(new AgentProjectImageOutput(
                    image,
                    normalized.Width,
                    normalized.Height,
                    $"{normalized.Width}x{normalized.Height}",
                    rasterMatched,
                    aspectMatched,
                    effectiveDpi));
            }
        }

        var succeeded = !timedOut && job.Status is ProjectImageGenerationJobStatus.Succeeded
            or ProjectImageGenerationJobStatus.CompletedWithErrors;
        var status = timedOut ? "timed_out" : StatusName(job.Status);
        var summary = timedOut
            ? "Image generation exceeded the configured lifetime and was cancelled; no image was placed."
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
            geometry is not null,
            outputImages,
            job.OutputErrors,
            summary);
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

    private static LayoutGeometry? ReadGeometry(ProjectImageJobView job)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(job.TargetGeometryJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("targetKind", out var targetKind)
                || targetKind.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(targetKind.GetString())
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
            return new LayoutGeometry(widthInches, heightInches, aspect);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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

    private sealed record LayoutGeometry(double WidthInches, double HeightInches, string AspectRatio);
}

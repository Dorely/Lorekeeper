using Lorekeeper.Models;
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
    string RequestedCanvas,
    IReadOnlyList<AgentProjectImageOutput> Outputs,
    IReadOnlyList<ProjectImageOutputErrorView> Diagnostics,
    string Summary)
{
    public IReadOnlyList<ProjectImageView> Images => Outputs.Select(output => output.Image).ToList();
}

public sealed record AgentProjectImageOutput(ProjectImageView Image, int Width, int Height);

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
        var outputImages = new List<AgentProjectImageOutput>();
        foreach (var imageId in job.OutputImageIds)
        {
            if (await images.GetAsync(projectId, imageId, cancellationToken) is { } image)
            {
                var data = await images.GetDataAsync(projectId, imageId, cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException($"Completed project image {imageId:N} has no readable data.");
                var normalized = ProjectImageBinary.Normalize(data.Data, data.ContentType, data.FileName, int.MaxValue);
                outputImages.Add(new AgentProjectImageOutput(image, normalized.Width, normalized.Height));
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
}

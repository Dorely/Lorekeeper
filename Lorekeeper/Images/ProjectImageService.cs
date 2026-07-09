using Lorekeeper.ChapterVisuals;
using Lorekeeper.Context;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lorekeeper.Images;

public sealed class ProjectImageService(
    AppDbContext db,
    IProjectImageJobService imageJobs,
    IProjectImageGenerationRuntime imageRuntime,
    IOptions<ProjectImageGenerationOptions> imageOptions,
    IChapterVisualService chapterVisuals,
    IContextIndexingService contextIndexing) : IProjectImageService
{
    public async Task<IReadOnlyList<ProjectImageView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await db.PublishAssets
            .AsNoTracking()
            .Where(asset => asset.ProjectId == projectId)
            .OrderByDescending(asset => asset.CreatedAt)
            .Select(asset => ToView(projectId, asset))
            .ToListAsync(cancellationToken);

    public async Task<ProjectImageView?> GetAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var asset = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);

        return asset is null ? null : ToView(projectId, asset);
    }

    public async Task<ProjectImageData?> GetDataAsync(
        Guid projectId,
        Guid imageId,
        int? maxEdge = null,
        CancellationToken cancellationToken = default)
    {
        var asset = await db.PublishAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);
        if (asset is null) return null;

        var data = maxEdge is int edge && edge > 0
            ? ProjectImageResize.Resize(asset.Data, asset.ContentType, edge)
            : asset.Data;

        return new ProjectImageData(asset.Id, asset.FileName, asset.ContentType, data, asset.AltText, asset.UpdatedAt);
    }

    public async Task<ProjectImageView> UploadAsync(Guid projectId, ProjectImageUpload upload, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var normalized = ProjectImageBinary.Normalize(upload.Data, upload.ContentType, upload.FileName);

        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = PublishAssetSource.Uploaded,
            FileName = normalized.FileName,
            ContentType = normalized.ContentType,
            Data = normalized.Data,
            AltText = Clean(upload.AltText),
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task<ProjectImageView> GenerateAsync(
        Guid projectId,
        ProjectImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new InvalidOperationException("Image prompt is required.");

        var job = await imageJobs.CreateGenerateJobAsync(projectId, new ProjectImageGenerateJobRequest(
            request.Prompt.Trim(),
            string.IsNullOrWhiteSpace(request.Size) ? imageOptions.Value.DefaultSize : request.Size.Trim(),
            string.IsNullOrWhiteSpace(request.Quality) ? imageOptions.Value.DefaultQuality : request.Quality.Trim(),
            string.IsNullOrWhiteSpace(request.OutputFormat) ? imageOptions.Value.DefaultOutputFormat : request.OutputFormat.Trim(),
            request.OutputCompression,
            Clean(request.AltText),
            Count: 1,
            request.ReferenceImageIds.Distinct().ToList(),
            Label: "Generated image",
            EntityTargets: request.EntityTargets), cancellationToken);

        await imageRuntime.EnqueueProjectAsync(projectId, cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(imageOptions.Value.AgentJobWaitTimeoutSeconds, 1, 3600));
        if (!await imageRuntime.WaitForJobCompletionAsync(job.Id, timeout, cancellationToken))
            throw new TimeoutException("Timed out waiting for the image generation job to complete.");

        var completed = await imageJobs.GetJobAsync(projectId, job.Id, cancellationToken)
            ?? throw new InvalidOperationException("Image generation job was not found after completion.");
        var outputId = completed.OutputImageIds.FirstOrDefault();
        if (outputId == Guid.Empty)
            throw new InvalidOperationException(completed.Error.Length > 0 ? completed.Error : "Image generation did not produce an output image.");

        return await GetAsync(projectId, outputId, cancellationToken)
            ?? throw new InvalidOperationException("Generated image was not found after completion.");
    }

    public async Task<ProjectImageView> UpdateAsync(
        Guid projectId,
        Guid imageId,
        ProjectImageUpdate update,
        CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken)
            ?? throw new InvalidOperationException("Image was not found.");

        var fileName = Clean(update.FileName);
        if (!string.IsNullOrWhiteSpace(fileName))
            asset.FileName = fileName;
        asset.AltText = Clean(update.AltText);
        asset.UpdatedAt = DateTime.UtcNow;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entityId in await AttachedEntityIdsAsync(projectId, imageId, cancellationToken))
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
        return ToView(projectId, asset);
    }

    public async Task DeleteAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken);
        if (asset is null) return;
        var entityIds = await AttachedEntityIdsAsync(projectId, imageId, cancellationToken);

        foreach (var profile in await db.PublishProfiles.Where(profile => profile.ProjectId == projectId && profile.SelectedCoverAssetId == imageId).ToListAsync(cancellationToken))
            profile.SelectedCoverAssetId = null;

        foreach (var placement in await db.PublishImagePlacements.Where(placement => placement.ProjectId == projectId && placement.AssetId == imageId).ToListAsync(cancellationToken))
            db.PublishImagePlacements.Remove(placement);

        await chapterVisuals.RemoveImageReferencesAsync(projectId, imageId, cancellationToken);

        db.PublishAssets.Remove(asset);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var entityId in entityIds)
            await contextIndexing.ReindexEntityAsync(projectId, entityId, cancellationToken);
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
        ?? throw new InvalidOperationException($"Project {projectId} not found.");

    public static ProjectImageView ToView(Guid projectId, PublishAsset asset) =>
        new(
            asset.Id,
            asset.FileName,
            asset.ContentType,
            $"/projects/{projectId:N}/images/{asset.Id:N}/content?maxEdge=640",
            asset.AltText,
            asset.Source,
            asset.Prompt,
            asset.GenerationModel,
            asset.SourceMetadataJson,
            asset.CreatedAt,
            asset.UpdatedAt,
            asset.Data.LongLength);

    private async Task<IReadOnlyList<Guid>> AttachedEntityIdsAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken)
    {
        var keys = await db.EntityVisualExamples
            .AsNoTracking()
            .Where(example => example.ProjectId == projectId && example.ImageId == imageId)
            .Select(example => example.GraphNode.Key)
            .ToListAsync(cancellationToken);
        return keys.Where(key => Guid.TryParseExact(key, "N", out _)).Select(key => Guid.ParseExact(key, "N")).ToList();
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorekeeper.Composition;
using Lorekeeper.EntityVisuals;
using Lorekeeper.Llm;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Lorekeeper.Images;

public sealed class ProjectImageJobService(
    IAppDatabaseOperationFactory database,
    IEntityVisualExampleService entityVisualExamples,
    IOptions<ProjectImageGenerationOptions> options) : IProjectImageJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<ProjectImageJobView>> ListJobsAsync(Guid projectId, int take = 25, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.ProjectImageGenerationJobs
                    .AsNoTracking()
                    .Where(job => job.ProjectId == projectId)
                    .OrderByDescending(job => job.CreatedAt)
                    .Take(Math.Clamp(take, 1, 100))
                    .Select(job => ToView(job))
                    .ToListAsync(cancellationToken);
    }
    public async Task<ProjectImageJobView?> GetJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == jobId, cancellationToken);
        return job is null ? null : ToView(job);
    }

    public async Task<ProjectImageJobView?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
        return job is null ? null : ToView(job);
    }

    public async Task<IReadOnlyList<ProjectImagePartialView>> ListPartialsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var partials = await databaseOperation.Db.ProjectImagePartials
            .AsNoTracking()
            .Where(partial => partial.ProjectId == projectId)
            .OrderByDescending(partial => partial.CreatedAt)
            .ThenBy(partial => partial.JobId)
            .ThenBy(partial => partial.OutputIndex)
            .ThenBy(partial => partial.Attempt)
            .ThenBy(partial => partial.PartialImageIndex)
            .Select(partial => new ProjectImagePartialView(
                partial.Id,
                partial.JobId,
                partial.OutputIndex,
                partial.Attempt,
                partial.PartialImageIndex,
                partial.FileName,
                partial.ContentType,
                string.Empty,
                partial.Width,
                partial.Height,
                partial.Provider,
                partial.MainlineModel,
                partial.ImageModel,
                partial.RequestId,
                partial.ResponseId,
                partial.CallId,
                partial.ItemId,
                partial.LastEventType,
                partial.EventCount,
                partial.FinalOutputImageId,
                partial.CreatedAt,
                partial.UpdatedAt))
            .ToListAsync(cancellationToken);
        return partials.Select(partial => WithPartialPreviewUrl(projectId, partial)).ToList();
    }

    public async Task<ProjectImagePartialData?> GetPartialDataAsync(
        Guid projectId,
        Guid jobId,
        Guid partialId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var partial = await databaseOperation.Db.ProjectImagePartials
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId
                && candidate.JobId == jobId
                && candidate.Id == partialId, cancellationToken);
        return partial is null
            ? null
            : new ProjectImagePartialData(
                partial.Id,
                partial.FileName,
                partial.ContentType,
                partial.Data,
                partial.Width,
                partial.Height,
                partial.UpdatedAt);
    }

    public async Task<ProjectImagePartialView> SavePartialAsync(
        Guid projectId,
        Guid jobId,
        int outputIndex,
        int attempt,
        ProjectImageProviderProgress progress,
        CancellationToken cancellationToken = default)
    {
        if (progress.Kind != ProjectImageProviderProgressKind.PartialImage)
            throw new InvalidOperationException("Only partial-image provider progress can be persisted.");
        if (outputIndex < 0 || attempt <= 0 || progress.PartialImageIndex is not { } partialImageIndex || partialImageIndex < 0)
            throw new InvalidDataException("Image partial metadata is missing a valid output, attempt, or partial index.");

        var payload = ParsePartialDataUrl(progress.PartialImageDataUrl, ResolveProviderOutputLimit());
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("Image generation job was not found.");
        if (outputIndex >= job.Count)
            throw new InvalidDataException("Image partial output index is outside the requested output count.");

        var existing = await db.ProjectImagePartials
            .FirstOrDefaultAsync(candidate => candidate.JobId == jobId
                && candidate.OutputIndex == outputIndex
                && candidate.Attempt == attempt
                && candidate.PartialImageIndex == partialImageIndex, cancellationToken);
        if (existing is not null)
            return ToPartialView(projectId, existing);

        var now = DateTime.UtcNow;
        var partial = new ProjectImagePartial
        {
            ProjectId = projectId,
            JobId = jobId,
            OutputIndex = outputIndex,
            Attempt = attempt,
            PartialImageIndex = partialImageIndex,
            FileName = PartialFileName(job.Label, outputIndex, attempt, partialImageIndex, payload.ContentType),
            ContentType = payload.ContentType,
            Data = payload.Data,
            Width = payload.Width,
            Height = payload.Height,
            Provider = job.Provider,
            MainlineModel = job.MainlineModel,
            ImageModel = job.ImageModel,
            RequestId = Clean(progress.RequestId),
            ResponseId = Clean(progress.ResponseId),
            CallId = Clean(progress.CallId),
            ItemId = Clean(progress.ItemId),
            LastEventType = Clean(progress.LastEventType),
            EventCount = Math.Max(0, progress.EventCount),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await db.ProjectImagePartials.AddAsync(partial, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToPartialView(projectId, partial);
    }

    public async Task<ProjectImageView> PromotePartialAsync(
        Guid projectId,
        Guid partialId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(projectId, cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var partial = await db.ProjectImagePartials
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == partialId, cancellationToken)
            ?? throw new InvalidOperationException("Image partial was not found or has already been promoted.");
        var job = await db.ProjectImageGenerationJobs
            .FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == partial.JobId, cancellationToken)
            ?? throw new InvalidOperationException("The image job for this partial was not found.");
        var project = await GetProjectAsync(projectId, cancellationToken);
        var now = DateTime.UtcNow;
        var storedImage = ProjectImageBinary.Normalize(
            partial.Data,
            partial.ContentType,
            partial.FileName,
            ResolveProviderOutputLimit());
        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = job.Kind == ProjectImageGenerationJobKind.Edit ? PublishAssetSource.Edited : PublishAssetSource.Generated,
            FileName = PromotionFileName(job.Label, partial.OutputIndex, partial.Attempt, partial.PartialImageIndex, storedImage.ContentType),
            ContentType = storedImage.ContentType,
            Data = storedImage.Data,
            AltText = job.AltText,
            Prompt = job.Prompt,
            GenerationModel = string.IsNullOrWhiteSpace(partial.ImageModel) ? job.ImageModel : partial.ImageModel,
            SourceMetadataJson = JsonSerializer.Serialize(new
            {
                PartialId = partial.Id,
                JobId = job.Id,
                JobKind = job.Kind.ToString(),
                partial.OutputIndex,
                partial.Attempt,
                partial.PartialImageIndex,
                partial.Provider,
                partial.MainlineModel,
                partial.ImageModel,
                partial.RequestId,
                partial.ResponseId,
                partial.CallId,
                partial.ItemId,
                partial.LastEventType,
                partial.EventCount,
                partial.FinalOutputImageId,
                OriginalRaster = new
                {
                    partial.ContentType,
                    partial.Width,
                    partial.Height,
                    SizeBytes = partial.Data.LongLength,
                },
                StoredRaster = new
                {
                    storedImage.ContentType,
                    storedImage.Width,
                    storedImage.Height,
                    SizeBytes = storedImage.Data.LongLength,
                },
            }, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        db.ProjectImagePartials.Remove(partial);
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ProjectImageService.ToView(projectId, asset);
    }

    public async Task<ProjectImageJobView> CreateGenerateJobAsync(
        Guid projectId,
        ProjectImageGenerateJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await GetProjectAsync(projectId, cancellationToken);
        var prompt = CleanRequired(request.Prompt, "Image prompt is required.");
        var referenceIds = await ValidateReferenceIdsAsync(projectId, request.ReferenceImageIds, sourceImageId: null, cancellationToken);
        var entityTargets = await ValidateEntityTargetsAsync(projectId, request.EntityTargets, cancellationToken);
        var count = ClampCount(request.Count);
        var layoutBound = HasLayoutTargetGeometry(request.TargetGeometryJson);
        var now = DateTime.UtcNow;
        var job = new ProjectImageGenerationJob
        {
            ProjectId = projectId,
            Kind = ProjectImageGenerationJobKind.Generate,
            Status = ProjectImageGenerationJobStatus.Queued,
            Label = Clean(request.Label) is { Length: > 0 } label ? label : $"Image {now:yyyy-MM-dd HH:mm:ss}",
            Prompt = prompt,
            BriefJson = CleanJson(request.BriefJson, "{}"),
            ReferenceManifestJson = CleanJson(request.ReferenceManifestJson, "[]"),
            TargetGeometryJson = CleanJson(request.TargetGeometryJson, "{}"),
            Size = NormalizeSize(request.Size),
            Quality = NormalizeQuality(request.Quality),
            OutputFormat = layoutBound ? "png" : NormalizeOutputFormat(request.OutputFormat),
            OutputCompression = layoutBound ? null : request.OutputCompression,
            Count = count,
            AltText = Clean(request.AltText),
            ReferenceImageIdsJson = SerializeIds(referenceIds),
            EntityVisualTargetsJson = SerializeTargets(entityTargets),
            InheritSourceEntityTargets = false,
            OutputStatesJson = SerializeOutputStates(CreateInitialOutputStates(count)),
            Provider = CodexProvider.Name,
            MainlineModel = options.Value.DefaultMainlineModel,
            ImageModel = options.Value.DefaultImageModel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.ProjectImageGenerationJobs.AddAsync(job, cancellationToken);
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(job);
    }

    public async Task<ProjectImageJobView> CreateEditJobAsync(
        Guid projectId,
        ProjectImageEditJobRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var project = await GetProjectAsync(projectId, cancellationToken);
        var prompt = CleanRequired(request.Prompt, "Image edit prompt is required.");
        var source = await GetImageAssetAsync(projectId, request.SourceImageId, cancellationToken);
        var referenceIds = await ValidateReferenceIdsAsync(projectId, request.ReferenceImageIds, source.Id, cancellationToken);
        var count = ClampCount(request.Count);
        var layoutBound = HasLayoutTargetGeometry(request.TargetGeometryJson);
        var targets = (await ValidateEntityTargetsAsync(projectId, request.EntityTargets, cancellationToken)).ToList();
        if (request.InheritSourceEntityTargets)
            targets.AddRange((await entityVisualExamples.ListForImageAsync(projectId, source.Id, cancellationToken))
                .Select(example => new EntityVisualTarget(example.EntityId, example.Label)));
        var now = DateTime.UtcNow;
        var jobId = Guid.NewGuid();
        ProjectImageMaskView? mask = null;
        if (!string.IsNullOrWhiteSpace(request.MaskPngDataUrl))
        {
            mask = await CreateMaskFromPngDataUrlAsync(
                projectId,
                source.Id,
                request.MaskPngDataUrl,
                $"{source.FileName} edit mask",
                "editJob",
                jobId,
                cancellationToken);
        }
        else if (request.ExistingMaskId is { } existingMaskId)
        {
            mask = await GetMaskAsync(projectId, existingMaskId, cancellationToken)
                ?? throw new InvalidOperationException("Mask was not found.");
            if (mask.ImageId != source.Id)
                throw new InvalidOperationException("Mask must be tied to the edit source image.");
        }

        var job = new ProjectImageGenerationJob
        {
            Id = jobId,
            ProjectId = projectId,
            Kind = ProjectImageGenerationJobKind.Edit,
            Status = ProjectImageGenerationJobStatus.Queued,
            Label = Clean(request.Label) is { Length: > 0 } label ? label : $"Edit {now:yyyy-MM-dd HH:mm:ss}",
            Prompt = prompt,
            BriefJson = CleanJson(request.BriefJson, "{}"),
            ReferenceManifestJson = CleanJson(request.ReferenceManifestJson, "[]"),
            TargetGeometryJson = CleanJson(request.TargetGeometryJson, "{}"),
            Size = NormalizeSize(request.Size),
            Quality = NormalizeQuality(request.Quality),
            OutputFormat = layoutBound ? "png" : NormalizeOutputFormat(request.OutputFormat),
            OutputCompression = layoutBound ? null : request.OutputCompression,
            Count = count,
            AltText = Clean(request.AltText),
            SourceImageId = source.Id,
            MaskId = mask?.Id,
            ReferenceImageIdsJson = SerializeIds(referenceIds),
            EntityVisualTargetsJson = SerializeTargets(targets),
            InheritSourceEntityTargets = request.InheritSourceEntityTargets,
            OutputStatesJson = SerializeOutputStates(CreateInitialOutputStates(count)),
            Provider = CodexProvider.Name,
            MainlineModel = options.Value.DefaultMainlineModel,
            ImageModel = options.Value.DefaultImageModel,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.ProjectImageGenerationJobs.AddAsync(job, cancellationToken);
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToView(job);
    }

    public async Task<ProjectImageGenerationWorkItem?> TryStartNextQueuedJobAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs
            .Where(candidate => candidate.ProjectId == projectId && candidate.Status == ProjectImageGenerationJobStatus.Queued)
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
            return null;

        job.Status = ProjectImageGenerationJobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToWorkItem(job);
    }

    public async Task<IReadOnlyList<Guid>> ListProjectsWithQueuedJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.ProjectImageGenerationJobs
                    .AsNoTracking()
                    .Where(job => job.Status == ProjectImageGenerationJobStatus.Queued)
                    .Select(job => job.ProjectId)
                    .Distinct()
                    .ToListAsync(cancellationToken);
    }
    public async Task CancelJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs.FirstOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.Id == jobId,
            cancellationToken);
        if (job is null || job.Status is ProjectImageGenerationJobStatus.Succeeded
            or ProjectImageGenerationJobStatus.CompletedWithErrors
            or ProjectImageGenerationJobStatus.Cancelled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var states = NormalizeOutputStates(job.OutputStatesJson, job.Count)
            .Select(state => state.Status is ProjectImageOutputStatus.Succeeded
                or ProjectImageOutputStatus.Failed
                or ProjectImageOutputStatus.Cancelled
                    ? state
                    : state with
                    {
                        Status = ProjectImageOutputStatus.Cancelled,
                        Message = "Image request cancelled.",
                        Error = string.Empty,
                        UpdatedAt = now,
                        CompletedAt = now,
                    })
            .ToList();

        job.Status = ProjectImageGenerationJobStatus.Cancelled;
        job.OutputStatesJson = SerializeOutputStates(states);
        job.Error = string.Empty;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkOutputStateAsync(Guid projectId, Guid jobId, ProjectImageOutputStateView outputState, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == jobId, cancellationToken);
        if (job is null
            || job.Status == ProjectImageGenerationJobStatus.Cancelled
            || outputState.OutputIndex < 0
            || outputState.OutputIndex >= job.Count)
            return;

        var states = UpsertOutputState(
            NormalizeOutputStates(job.OutputStatesJson, job.Count),
            CleanOutputState(outputState));
        job.OutputStatesJson = SerializeOutputStates(states);
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkOutputFailedAsync(Guid projectId, Guid jobId, ProjectImageOutputErrorView outputError, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == jobId, cancellationToken);
        if (job is null
            || job.Status == ProjectImageGenerationJobStatus.Cancelled
            || outputError.OutputIndex < 0
            || outputError.OutputIndex >= job.Count)
            return;

        var errors = DeserializeOutputErrors(job.OutputErrorsJson)
            .Where(error => error.OutputIndex != outputError.OutputIndex)
            .Append(outputError)
            .OrderBy(error => error.OutputIndex)
            .ToList();
        job.OutputErrorsJson = JsonSerializer.Serialize(errors, JsonOptions);
        job.OutputStatesJson = SerializeOutputStates(UpsertOutputState(
            NormalizeOutputStates(job.OutputStatesJson, job.Count),
            new ProjectImageOutputStateView(
                outputError.OutputIndex,
                ProjectImageOutputStatus.Failed,
                Message: "Image request failed.",
                Error: outputError.Error,
                ErrorKind: outputError.ErrorKind,
                RequestId: outputError.RequestId,
                ResponseId: outputError.ResponseId,
                CallId: outputError.CallId,
                LastEventType: outputError.LastEventType,
                EventCount: outputError.EventCount,
                UpdatedAt: DateTime.UtcNow,
                CompletedAt: DateTime.UtcNow)));
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProjectImageView> SaveGeneratedOutputAsync(
        Guid projectId,
        Guid jobId,
        int outputIndex,
        ProjectImageProviderResult result,
        ProjectImageProviderImage image,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("Image generation job was not found.");
        if (job.Status == ProjectImageGenerationJobStatus.Cancelled)
            throw new OperationCanceledException("Image generation job was cancelled before its output could be saved.");
        var project = await GetProjectAsync(projectId, cancellationToken);
        var referenceIds = DeserializeIds(job.ReferenceImageIdsJson);
        var source = job.SourceImageId is Guid sourceImageId
            ? await db.PublishAssets.AsNoTracking().FirstOrDefaultAsync(asset => asset.ProjectId == projectId && asset.Id == sourceImageId, cancellationToken)
            : null;
        var references = referenceIds.Count == 0
            ? []
            : await db.PublishAssets.AsNoTracking()
                .Where(asset => asset.ProjectId == projectId && referenceIds.Contains(asset.Id))
                .Select(asset => new { asset.Id, asset.FileName, asset.ContentType })
                .ToListAsync(cancellationToken);
        var mask = job.MaskId is Guid maskId
            ? await db.ProjectImageMasks.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == maskId, cancellationToken)
            : null;
        var storedImage = ProjectImageBinary.Normalize(
            image.Data,
            image.ContentType,
            $"provider-output.{image.OutputFormat}",
            ResolveProviderOutputLimit());
        var layoutBound = HasLayoutTargetGeometry(job.TargetGeometryJson);
        var hasRequestedRaster = LayoutImageSizeResolver.TryParse(job.Size, out var requestedRaster);
        if (layoutBound && !hasRequestedRaster)
            throw new InvalidDataException("Layout-bound image jobs require an explicit requested raster.");
        var rasterMatched = !hasRequestedRaster
            || storedImage.Width == requestedRaster.Width && storedImage.Height == requestedRaster.Height;
        var aspectMatched = TryReadTargetAspect(job.TargetGeometryJson, out var targetAspect)
            ? LayoutImageSizeResolver.AspectMatches((double)storedImage.Width / storedImage.Height, targetAspect)
            : !hasRequestedRaster
                || LayoutImageSizeResolver.AspectMatches((double)storedImage.Width / storedImage.Height, (double)requestedRaster.Width / requestedRaster.Height);
        var now = DateTime.UtcNow;
        var asset = new PublishAsset
        {
            ProjectId = projectId,
            Source = job.Kind == ProjectImageGenerationJobKind.Edit ? PublishAssetSource.Edited : PublishAssetSource.Generated,
            FileName = $"{SafeFileNameStem(job.Label, job.Kind == ProjectImageGenerationJobKind.Edit ? "edited" : "generated")}-{outputIndex + 1}.{ExtensionForContentType(storedImage.ContentType)}",
            ContentType = storedImage.ContentType,
            Data = storedImage.Data,
            AltText = job.AltText,
            Prompt = job.Prompt,
            GenerationModel = result.ImageModel,
            SourceMetadataJson = JsonSerializer.Serialize(new
            {
                JobId = job.Id,
                JobKind = job.Kind.ToString(),
                OutputIndex = outputIndex,
                result.Provider,
                result.MainlineModel,
                result.ImageModel,
                image.OutputFormat,
                image.RevisedPrompt,
                StructuredBrief = JsonNodeOrString(job.BriefJson),
                ReferenceManifest = JsonNodeOrString(job.ReferenceManifestJson),
                TargetGeometry = JsonNodeOrString(job.TargetGeometryJson),
                image.ResponseId,
                image.CallId,
                RasterStorage = new
                {
                    image.ContentType,
                    StoredContentType = storedImage.ContentType,
                    storedImage.Width,
                    storedImage.Height,
                    LayoutTransform = "none",
                },
                GeometryValidation = new
                {
                    LayoutBound = layoutBound,
                    RequestedRaster = hasRequestedRaster ? requestedRaster.Size : job.Size,
                    ActualRaster = $"{storedImage.Width}x{storedImage.Height}",
                    RasterMatched = rasterMatched,
                    AspectMatched = aspectMatched,
                    WarningCode = !rasterMatched
                        ? "PROVIDER_IMAGE_RASTER_MISMATCH"
                        : !aspectMatched ? "LAYOUT_IMAGE_ASPECT_MISMATCH" : null,
                },
                SourceImage = source is null ? null : new { source.Id, source.FileName, source.ContentType },
                Mask = mask is null ? null : new { mask.Id, mask.Label, mask.ContentType, mask.Width, mask.Height },
                ReferenceImages = references,
            }, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.PublishAssets.AddAsync(asset, cancellationToken);
        var partials = await db.ProjectImagePartials
            .Where(partial => partial.ProjectId == projectId
                && partial.JobId == jobId
                && partial.OutputIndex == outputIndex
                && partial.FinalOutputImageId == null)
            .ToListAsync(cancellationToken);
        foreach (var partial in partials)
        {
            partial.FinalOutputImageId = asset.Id;
            partial.UpdatedAt = now;
        }
        job.Provider = result.Provider;
        job.MainlineModel = result.MainlineModel;
        job.ImageModel = result.ImageModel;
        job.RawProviderResponseJson = result.RawMetadataJson;
        if (!string.IsNullOrWhiteSpace(image.RevisedPrompt))
        {
            var revisedPrompts = DeserializeStrings(job.ProviderRevisedPromptsJson);
            revisedPrompts.Add(image.RevisedPrompt.Trim());
            job.ProviderRevisedPromptsJson = JsonSerializer.Serialize(revisedPrompts, JsonOptions);
        }
        job.OutputImageIdsJson = SerializeIds(DeserializeIds(job.OutputImageIdsJson).Append(asset.Id));
        job.OutputStatesJson = SerializeOutputStates(UpsertOutputState(
            NormalizeOutputStates(job.OutputStatesJson, job.Count),
            new ProjectImageOutputStateView(
                outputIndex,
                ProjectImageOutputStatus.Succeeded,
                Message: "Image saved.",
                UpdatedAt: now,
                CompletedAt: now)));
        job.UpdatedAt = now;
        project.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var target in DeserializeTargets(job.EntityVisualTargetsJson))
            await entityVisualExamples.AttachAsync(projectId, target.EntityId, asset.Id, target.Label, EntityVisualExampleOrigin.Agent, cancellationToken: cancellationToken);
        return ProjectImageService.ToView(projectId, asset);
    }

    private int ResolveProviderOutputLimit()
    {
        if (options.Value.MaxProviderOutputBytes <= 0)
            throw new InvalidOperationException("The image provider output limit must be greater than zero bytes.");

        return options.Value.MaxProviderOutputBytes;
    }

    public async Task CompleteJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var job = await db.ProjectImageGenerationJobs.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("Image generation job was not found.");
        if (job.Status == ProjectImageGenerationJobStatus.Cancelled)
        {
            job.CompletedAt ??= DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var states = NormalizeOutputStates(job.OutputStatesJson, job.Count);
        var failed = states.Count(state => state.Status is ProjectImageOutputStatus.Failed or ProjectImageOutputStatus.Cancelled);
        var succeeded = states.Count(state => state.Status == ProjectImageOutputStatus.Succeeded);
        job.Status = succeeded > 0 && failed == 0
            ? ProjectImageGenerationJobStatus.Succeeded
            : succeeded > 0
                ? ProjectImageGenerationJobStatus.CompletedWithErrors
                : ProjectImageGenerationJobStatus.Failed;
        if (job.Status == ProjectImageGenerationJobStatus.Failed && string.IsNullOrWhiteSpace(job.Error))
            job.Error = "Image generation failed.";
        job.CompletedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkInterruptedRunningJobsFailedAsync(CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var running = await db.ProjectImageGenerationJobs
            .Where(job => job.Status == ProjectImageGenerationJobStatus.Running)
            .ToListAsync(cancellationToken);
        if (running.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var job in running)
        {
            job.Status = ProjectImageGenerationJobStatus.Failed;
            job.Error = "Image generation was interrupted before it completed.";
            job.CompletedAt = now;
            job.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProjectImageMaskView?> GetMaskAsync(Guid projectId, Guid maskId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var mask = await db.ProjectImageMasks.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == maskId, cancellationToken);
        return mask is null ? null : ToMaskView(mask);
    }

    public async Task<ProjectImageData?> GetMaskDataAsync(Guid projectId, Guid maskId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var mask = await db.ProjectImageMasks.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == maskId, cancellationToken);
        return mask is null
            ? null
            : new ProjectImageData(mask.Id, string.IsNullOrWhiteSpace(mask.Label) ? $"mask-{mask.Id:N}.png" : SafeFileNameStem(mask.Label, "mask") + ".png", mask.ContentType, mask.Data, string.Empty, mask.UpdatedAt);
    }

    public async Task<ProjectImageMaskView> CreateMaskFromPngDataUrlAsync(
        Guid projectId,
        Guid imageId,
        string maskPngDataUrl,
        string label,
        string ownerKind,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var db = databaseOperation.Db;
        var source = await GetImageAssetAsync(projectId, imageId, cancellationToken);
        var payload = ParsePngDataUrl(maskPngDataUrl, "Mask must be a PNG data URL.");
        var sourceSize = ReadImageSize(source.Data, source.ContentType);
        if (sourceSize.Width != payload.Width || sourceSize.Height != payload.Height)
            throw new InvalidOperationException("Mask dimensions must match the source image dimensions.");
        EnsurePngMaskHasEditableArea(payload.Data);

        var now = DateTime.UtcNow;
        var mask = new ProjectImageMask
        {
            ProjectId = projectId,
            ImageId = imageId,
            Label = Clean(label) is { Length: > 0 } cleanLabel ? cleanLabel : $"{source.FileName} mask",
            ContentType = payload.ContentType,
            Data = payload.Data,
            Width = payload.Width,
            Height = payload.Height,
            OwnerKind = Clean(ownerKind) is { Length: > 0 } cleanOwner ? cleanOwner : "image",
            OwnerId = ownerId == Guid.Empty ? imageId : ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await db.ProjectImageMasks.AddAsync(mask, cancellationToken);
        await TouchProjectAsync(projectId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToMaskView(mask);
    }

    public async Task<ProjectImageMaskView> CreateMaskFromShapesAsync(
        Guid projectId,
        Guid imageId,
        ProjectImageMaskShapeRequest request,
        CancellationToken cancellationToken = default)
    {
        var source = await GetImageAssetAsync(projectId, imageId, cancellationToken);
        var sourceSize = ReadImageSize(source.Data, source.ContentType);
        var data = RenderShapeMask(sourceSize.Width, sourceSize.Height, request.Shapes);
        return await CreateMaskFromPngDataUrlAsync(
            projectId,
            imageId,
            DataUrl.ToDataUrl("image/png", data),
            request.Label,
            "agentShape",
            Guid.NewGuid(),
            cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> ValidateReferenceIdsAsync(
        Guid projectId,
        IReadOnlyList<Guid> requestedIds,
        Guid? sourceImageId,
        CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var ids = requestedIds.Where(id => id != Guid.Empty && id != sourceImageId).Distinct().ToList();
        if (ids.Count > Math.Max(0, options.Value.MaxReferenceImages))
            throw new InvalidOperationException($"Select no more than {Math.Max(0, options.Value.MaxReferenceImages)} reference images.");
        if (ids.Count == 0)
            return ids;

        var found = await db.PublishAssets
            .Where(asset => asset.ProjectId == projectId && ids.Contains(asset.Id))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        if (found.Count != ids.Count)
        {
            var foundIds = found.ToHashSet();
            var missingIds = ids.Where(id => !foundIds.Contains(id)).Select(id => id.ToString("D"));
            throw new InvalidOperationException($"Reference image ids not found in this project: {string.Join(", ", missingIds)}.");
        }

        return ids;
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        return await db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
                ?? throw new InvalidOperationException($"Project {projectId} not found.");
    }
    private async Task<PublishAsset> GetImageAssetAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var asset = await db.PublishAssets.FirstOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Id == imageId, cancellationToken)
            ?? throw new InvalidOperationException("Image was not found.");
        if (NormalizeImageContentType(asset.ContentType) is null)
            throw new InvalidOperationException("Image must be a PNG or JPEG image.");
        return asset;
    }

    private async Task TouchProjectAsync(Guid projectId, DateTime now, CancellationToken cancellationToken)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var db = databaseOperation.Db;
        var project = await db.Projects.FirstOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        if (project is not null)
            project.UpdatedAt = now;
    }

    private ProjectImageGenerationWorkItem ToWorkItem(ProjectImageGenerationJob job) =>
        new(
            job.ProjectId,
            job.Id,
            job.Kind,
            job.Prompt,
            job.Size,
            job.Quality,
            job.OutputFormat,
            job.OutputCompression,
            job.Count,
            job.AltText,
            job.Label,
            job.SourceImageId,
            job.MaskId,
            DeserializeIds(job.ReferenceImageIdsJson),
            string.IsNullOrWhiteSpace(job.MainlineModel) ? options.Value.DefaultMainlineModel : job.MainlineModel,
            string.IsNullOrWhiteSpace(job.ImageModel) ? options.Value.DefaultImageModel : job.ImageModel);

    private static ProjectImageJobView ToView(ProjectImageGenerationJob job) =>
        new(
            job.Id,
            job.Kind,
            job.Status,
            job.Label,
            job.Prompt,
            job.Size,
            job.Quality,
            job.OutputFormat,
            job.OutputCompression,
            job.Count,
            job.AltText,
            job.SourceImageId,
            job.MaskId,
            DeserializeIds(job.ReferenceImageIdsJson),
            DeserializeIds(job.OutputImageIdsJson),
            NormalizeOutputStates(job.OutputStatesJson, job.Count),
            DeserializeOutputErrors(job.OutputErrorsJson),
            job.Provider,
            job.MainlineModel,
            job.ImageModel,
            job.Error,
            job.CreatedAt,
            job.UpdatedAt,
            job.StartedAt,
            job.CompletedAt,
            DeserializeTargets(job.EntityVisualTargetsJson),
            job.InheritSourceEntityTargets,
            job.BriefJson,
            job.ReferenceManifestJson,
            job.TargetGeometryJson,
            DeserializeStrings(job.ProviderRevisedPromptsJson));

    private static ProjectImagePartialView WithPartialPreviewUrl(Guid projectId, ProjectImagePartialView partial) =>
        partial with
        {
            PreviewUrl = $"/projects/{projectId:N}/image-jobs/{partial.JobId:N}/partials/{partial.Id:N}/content?maxEdge=640",
        };

    private static ProjectImagePartialView ToPartialView(Guid projectId, ProjectImagePartial partial) =>
        new(
            partial.Id,
            partial.JobId,
            partial.OutputIndex,
            partial.Attempt,
            partial.PartialImageIndex,
            partial.FileName,
            partial.ContentType,
            $"/projects/{projectId:N}/image-jobs/{partial.JobId:N}/partials/{partial.Id:N}/content?maxEdge=640",
            partial.Width,
            partial.Height,
            partial.Provider,
            partial.MainlineModel,
            partial.ImageModel,
            partial.RequestId,
            partial.ResponseId,
            partial.CallId,
            partial.ItemId,
            partial.LastEventType,
            partial.EventCount,
            partial.FinalOutputImageId,
            partial.CreatedAt,
            partial.UpdatedAt);

    private static string SerializeTargets(IEnumerable<EntityVisualTarget>? targets) => JsonSerializer.Serialize(
        (targets ?? []).Where(target => target.EntityId != Guid.Empty).DistinctBy(target => target.EntityId).ToList(), JsonOptions);

    private static IReadOnlyList<EntityVisualTarget> DeserializeTargets(string json)
    {
        try { return JsonSerializer.Deserialize<List<EntityVisualTarget>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    private async Task<IReadOnlyList<EntityVisualTarget>> ValidateEntityTargetsAsync(
        Guid projectId,
        IEnumerable<EntityVisualTarget>? targets,
        CancellationToken cancellationToken)
    {
        var validation = await entityVisualExamples.ValidateTargetsAsync(
            projectId,
            (targets ?? []).ToList(),
            cancellationToken);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.Error);
        return validation.Targets;
    }

    private static ProjectImageMaskView ToMaskView(ProjectImageMask mask) =>
        new(
            mask.Id,
            mask.ImageId,
            mask.Label,
            mask.ContentType,
            $"/projects/{mask.ProjectId:N}/image-masks/{mask.Id:N}/content",
            mask.Width,
            mask.Height,
            mask.CreatedAt,
            mask.UpdatedAt);

    private int ClampCount(int count) =>
        Math.Clamp(count <= 0 ? 1 : count, 1, Math.Max(1, options.Value.MaxOutputs));

    private static string CleanRequired(string value, string message)
    {
        var clean = Clean(value);
        if (string.IsNullOrWhiteSpace(clean))
            throw new InvalidOperationException(message);
        return clean;
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private static string NormalizeSize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();

    private static string CleanJson(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        _ = JsonNode.Parse(value) ?? throw new InvalidOperationException("Structured image audit JSON cannot be null.");
        return value.Trim();
    }

    private static object JsonNodeOrString(string value)
    {
        try { return JsonNode.Parse(value) ?? value; }
        catch (JsonException) { return value; }
    }

    private static List<string> DeserializeStrings(string value)
    {
        try { return JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string NormalizeQuality(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => "auto",
        };

    private static string NormalizeOutputFormat(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "jpeg" => "jpeg",
            "webp" => "webp",
            _ => "png",
        };

    private static bool HasLayoutTargetGeometry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (document.RootElement.TryGetProperty("layoutBound", out var layoutBound)
                && layoutBound.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return layoutBound.GetBoolean();
            return document.RootElement.TryGetProperty("targetKind", out var targetKind)
                && targetKind.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(targetKind.GetString())
                && document.RootElement.TryGetProperty("widthInches", out var width)
                && width.ValueKind == JsonValueKind.Number
                && width.TryGetDouble(out var widthInches)
                && widthInches > 0
                && document.RootElement.TryGetProperty("heightInches", out var height)
                && height.ValueKind == JsonValueKind.Number
                && height.TryGetDouble(out var heightInches)
                && heightInches > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadTargetAspect(string? value, out double aspect)
    {
        aspect = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (!root.TryGetProperty("widthInches", out var width)
                || !width.TryGetDouble(out var widthInches)
                || widthInches <= 0
                || !root.TryGetProperty("heightInches", out var height)
                || !height.TryGetDouble(out var heightInches)
                || heightInches <= 0)
            {
                return false;
            }
            aspect = widthInches / heightInches;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeImageContentType(string contentType) =>
        contentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            "image/jpg" => "image/jpeg",
            _ => null,
        };

    private static string SerializeIds(IEnumerable<Guid> ids) =>
        JsonSerializer.Serialize(ids.Where(id => id != Guid.Empty).Distinct().ToList(), JsonOptions);

    private static List<Guid> DeserializeIds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeOutputStates(IEnumerable<ProjectImageOutputStateView> states) =>
        JsonSerializer.Serialize(states.Select(CleanOutputState).OrderBy(state => state.OutputIndex).ToList(), JsonOptions);

    private static List<ProjectImageOutputStateView> NormalizeOutputStates(string value, int count)
    {
        List<ProjectImageOutputStateView> states;
        try
        {
            states = string.IsNullOrWhiteSpace(value)
                ? []
                : JsonSerializer.Deserialize<List<ProjectImageOutputStateView>>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            states = [];
        }

        var byIndex = states
            .Where(state => state.OutputIndex >= 0 && state.OutputIndex < count)
            .GroupBy(state => state.OutputIndex)
            .ToDictionary(group => group.Key, group => CleanOutputState(group.Last()));
        for (var i = 0; i < count; i++)
            byIndex.TryAdd(i, new ProjectImageOutputStateView(i, ProjectImageOutputStatus.Queued, Message: "Waiting for image request."));

        return byIndex.Values.OrderBy(state => state.OutputIndex).ToList();
    }

    private static List<ProjectImageOutputStateView> CreateInitialOutputStates(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new ProjectImageOutputStateView(index, ProjectImageOutputStatus.Queued, Message: "Waiting for image request."))
            .ToList();

    private static List<ProjectImageOutputStateView> UpsertOutputState(IEnumerable<ProjectImageOutputStateView> states, ProjectImageOutputStateView state) =>
        states
            .Where(candidate => candidate.OutputIndex != state.OutputIndex)
            .Append(state)
            .OrderBy(candidate => candidate.OutputIndex)
            .ToList();

    private static ProjectImageOutputStateView CleanOutputState(ProjectImageOutputStateView state) =>
        state with
        {
            Message = Clean(state.Message),
            Error = Clean(state.Error),
            ErrorKind = Clean(state.ErrorKind),
            RequestId = Clean(state.RequestId),
            ResponseId = Clean(state.ResponseId),
            CallId = Clean(state.CallId),
            LastEventType = Clean(state.LastEventType),
        };

    private static List<ProjectImageOutputErrorView> DeserializeOutputErrors(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ProjectImageOutputErrorView>>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ExtensionForContentType(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ? "webp" : "png";

    private static ImagePayload ParsePartialDataUrl(string? dataUrl, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
            throw new InvalidDataException("Image partial did not contain image data.");

        string contentType;
        byte[] data;
        try
        {
            (contentType, data) = DataUrl.Parse(dataUrl);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidDataException("Image partial was not a valid base64 data URL.", ex);
        }

        if (data.Length == 0)
            throw new InvalidDataException("Image partial was empty.");
        if (data.Length > maxBytes)
            throw new InvalidDataException($"Image partial exceeds the configured {maxBytes / 1024 / 1024:N0} MB provider-output limit.");

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Image partial is not a supported raster image.");
        var encodedContentType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw new InvalidDataException("Image partial must be PNG, JPEG, or WebP."),
        };
        using var bitmap = SKBitmap.Decode(data) ?? throw new InvalidDataException("Image partial could not be decoded.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidDataException("Image partial dimensions are invalid.");

        _ = contentType;
        return new ImagePayload(encodedContentType, data, bitmap.Width, bitmap.Height);
    }

    private static string PartialFileName(string label, int outputIndex, int attempt, int partialImageIndex, string contentType) =>
        $"{SafeFileNameStem(label, "partial")}-output-{outputIndex + 1}-attempt-{attempt}-partial-{partialImageIndex + 1}.{ExtensionForContentType(contentType)}";

    private static string PromotionFileName(string label, int outputIndex, int attempt, int partialImageIndex, string contentType) =>
        $"{SafeFileNameStem(label, "partial")}-output-{outputIndex + 1}-attempt-{attempt}-partial-{partialImageIndex + 1}.{ExtensionForContentType(contentType)}";

    private static string SafeFileNameStem(string value, string fallback)
    {
        var stem = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            stem = stem.Replace(invalid, '-');
        stem = stem.Replace(' ', '-').Trim('-').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(stem) ? fallback : stem;
    }

    private static ImagePayload ParsePngDataUrl(string dataUrl, string error)
    {
        string contentType;
        byte[] data;
        try
        {
            (contentType, data) = DataUrl.Parse(dataUrl);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(error, ex);
        }

        if (!contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(error);
        var size = ReadImageSize(data, contentType);
        return new ImagePayload(contentType, data, size.Width, size.Height);
    }

    private static ImageSize ReadImageSize(byte[] data, string contentType)
    {
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidOperationException($"{contentType} dimensions could not be read.");
        return new ImageSize(bitmap.Width, bitmap.Height);
    }

    private static void EnsurePngMaskHasEditableArea(byte[] data)
    {
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidOperationException("Mask PNG dimensions could not be read.");
        if (bitmap.AlphaType == SKAlphaType.Opaque)
            throw new InvalidOperationException("Mask PNG must contain an alpha channel.");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < byte.MaxValue)
                    return;
            }
        }

        throw new InvalidOperationException("Paint the editable mask area first.");
    }

    private static byte[] RenderShapeMask(int width, int height, IReadOnlyList<ProjectImageMaskShape> shapes)
    {
        if (shapes.Count == 0)
            throw new InvalidOperationException("At least one mask shape is required.");

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        using var clearPaint = new SKPaint
        {
            BlendMode = SKBlendMode.Clear,
            IsAntialias = true,
        };

        foreach (var shape in shapes)
        {
            var kind = shape.Kind.Trim().ToLowerInvariant();
            if (kind == "ellipse")
            {
                canvas.DrawOval(PercentRect(shape.X, shape.Y, shape.Width, shape.Height, width, height), clearPaint);
            }
            else if (kind == "polygon")
            {
                var points = shape.Points ?? [];
                if (points.Count < 3)
                    throw new InvalidOperationException("Polygon masks require at least three points.");
                using var path = new SKPath();
                path.MoveTo(PercentX(points[0].X, width), PercentY(points[0].Y, height));
                foreach (var point in points.Skip(1))
                    path.LineTo(PercentX(point.X, width), PercentY(point.Y, height));
                path.Close();
                canvas.DrawPath(path, clearPaint);
            }
            else
            {
                canvas.DrawRect(PercentRect(shape.X, shape.Y, shape.Width, shape.Height, width, height), clearPaint);
            }
        }

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static SKRect PercentRect(double x, double y, double width, double height, int imageWidth, int imageHeight)
    {
        var left = PercentX(x, imageWidth);
        var top = PercentY(y, imageHeight);
        var right = PercentX(x + Math.Max(0, width), imageWidth);
        var bottom = PercentY(y + Math.Max(0, height), imageHeight);
        return new SKRect(
            Math.Min(left, right),
            Math.Min(top, bottom),
            Math.Max(left, right),
            Math.Max(top, bottom));
    }

    private static float PercentX(double value, int width) =>
        (float)(Math.Clamp(value, 0, 100) / 100 * width);

    private static float PercentY(double value, int height) =>
        (float)(Math.Clamp(value, 0, 100) / 100 * height);

    private sealed record ImagePayload(string ContentType, byte[] Data, int Width, int Height);

    private sealed record ImageSize(int Width, int Height);
}

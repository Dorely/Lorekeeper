using Lorekeeper.Models;

namespace Lorekeeper.Images;

public interface IProjectImageJobService
{
    Task<IReadOnlyList<ProjectImageJobView>> ListJobsAsync(Guid projectId, int take = 25, CancellationToken cancellationToken = default);
    Task<ProjectImageJobView?> GetJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
    Task<ProjectImageJobView?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectImagePartialView>> ListPartialsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectImagePartialData?> GetPartialDataAsync(Guid projectId, Guid jobId, Guid partialId, CancellationToken cancellationToken = default);
    Task<ProjectImagePartialView> SavePartialAsync(Guid projectId, Guid jobId, int outputIndex, int attempt, ProjectImageProviderProgress progress, CancellationToken cancellationToken = default);
    Task<ProjectImageView> PromotePartialAsync(Guid projectId, Guid partialId, CancellationToken cancellationToken = default);
    Task DeleteUnpromotedPartialsAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
    Task<ProjectImageJobView> CreateGenerateJobAsync(Guid projectId, ProjectImageGenerateJobRequest request, CancellationToken cancellationToken = default);
    Task<ProjectImageJobView> CreateEditJobAsync(Guid projectId, ProjectImageEditJobRequest request, CancellationToken cancellationToken = default);
    Task<ProjectImageGenerationWorkItem?> TryStartNextQueuedJobAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListProjectsWithQueuedJobsAsync(CancellationToken cancellationToken = default);
    Task CancelJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
    Task MarkOutputStateAsync(Guid projectId, Guid jobId, ProjectImageOutputStateView outputState, CancellationToken cancellationToken = default);
    Task MarkOutputFailedAsync(Guid projectId, Guid jobId, ProjectImageOutputErrorView outputError, CancellationToken cancellationToken = default);
    Task<ProjectImageView> SaveGeneratedOutputAsync(Guid projectId, Guid jobId, int outputIndex, ProjectImageProviderResult result, ProjectImageProviderImage image, CancellationToken cancellationToken = default);
    Task CompleteJobAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);
    Task MarkInterruptedRunningJobsFailedAsync(CancellationToken cancellationToken = default);
    Task<ProjectImageData?> GetMaskDataAsync(Guid projectId, Guid maskId, CancellationToken cancellationToken = default);
}

public sealed record ProjectImageGenerationWorkItem(
    Guid ProjectId,
    Guid JobId,
    ProjectImageGenerationJobKind Kind,
    string Prompt,
    string Size,
    string Quality,
    string OutputFormat,
    int? OutputCompression,
    int Count,
    string AltText,
    string Label,
    Guid? SourceImageId,
    Guid? MaskId,
    IReadOnlyList<Guid> ReferenceImageIds,
    string TargetGeometryJson,
    string MainlineModel,
    string ImageModel);

public sealed record ProjectImageMaskShapeRequest(
    string Label,
    IReadOnlyList<ProjectImageMaskShape> Shapes);

public sealed record ProjectImageMaskShape(
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<ProjectImageMaskPoint>? Points = null);

public sealed record ProjectImageMaskPoint(double X, double Y);

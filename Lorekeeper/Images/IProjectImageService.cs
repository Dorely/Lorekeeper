namespace Lorekeeper.Images;

public interface IProjectImageService
{
    Task<IReadOnlyList<ProjectImageView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectImageView>> ListByIdsAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> imageIds,
        CancellationToken cancellationToken = default);
    Task<ProjectImageView?> GetAsync(Guid projectId, Guid imageId, CancellationToken cancellationToken = default);
    Task<ProjectImageData?> GetDataAsync(Guid projectId, Guid imageId, int? maxEdge = null, CancellationToken cancellationToken = default);
    Task<ProjectImageView> UploadAsync(Guid projectId, ProjectImageUpload upload, CancellationToken cancellationToken = default);
    Task<ProjectImageView> CropAsync(Guid projectId, Guid sourceImageId, ProjectImageCropRequest request, CancellationToken cancellationToken = default);
    Task<ProjectImageView> GenerateAsync(Guid projectId, ProjectImageGenerationRequest request, CancellationToken cancellationToken = default);
    Task<ProjectImageView> UpdateAsync(Guid projectId, Guid imageId, ProjectImageUpdate update, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid projectId, Guid imageId, bool clearAffectedHistory = false, CancellationToken cancellationToken = default);
}

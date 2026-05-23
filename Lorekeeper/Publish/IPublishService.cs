using Lorekeeper.ImportExport;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public interface IPublishService
{
    Task<PublishWorkspaceView> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(Guid projectId, PublishProfileUpdate update, CancellationToken cancellationToken = default);
    Task SetCoverAssetAsync(Guid projectId, Guid? assetId, CancellationToken cancellationToken = default);
    Task SetOutlineSelectionAsync(Guid projectId, PublishOutlineTargetKind targetKind, Guid targetId, bool isIncluded, CancellationToken cancellationToken = default);
    Task<PublishAssetView> UploadAssetAsync(Guid projectId, PublishAssetUpload upload, CancellationToken cancellationToken = default);
    Task<PublishAssetView> GenerateImageAsync(Guid projectId, PublishImageGenerationRequest request, CancellationToken cancellationToken = default);
    Task DeleteAssetAsync(Guid projectId, Guid assetId, CancellationToken cancellationToken = default);
    Task AddImagePlacementAsync(Guid projectId, PublishImagePlacementCreate request, CancellationToken cancellationToken = default);
    Task DeleteImagePlacementAsync(Guid projectId, Guid placementId, CancellationToken cancellationToken = default);
    Task<PublishDocument> GetDocumentAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectExportFile> ExportAsync(Guid projectId, PublishExportFormat format, CancellationToken cancellationToken = default);
}

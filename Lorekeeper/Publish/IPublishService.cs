using Lorekeeper.ImportExport;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public interface IPublishService
{
    Task<PublishWorkspaceView> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task SaveProfileAsync(Guid projectId, PublishProfileUpdate update, CancellationToken cancellationToken = default);
    Task SetCoverChapterAsync(Guid projectId, Guid? chapterId, CancellationToken cancellationToken = default);
    Task SetOutlineSelectionAsync(Guid projectId, PublishOutlineTargetKind targetKind, Guid targetId, bool isIncluded, CancellationToken cancellationToken = default);
    Task SetOutlineSelectionsAsync(Guid projectId, IReadOnlyList<PublishOutlineSelectionUpdate> updates, CancellationToken cancellationToken = default);
    Task<PublishImagePlacementView> AddImagePlacementAsync(Guid projectId, PublishImagePlacementCreate request, CancellationToken cancellationToken = default);
    Task<PublishImagePlacementView> UpdateImagePlacementAsync(Guid projectId, Guid placementId, PublishImagePlacementUpdate request, CancellationToken cancellationToken = default);
    Task ReorderImagePlacementsAsync(Guid projectId, IReadOnlyList<Guid> orderedPlacementIds, CancellationToken cancellationToken = default);
    Task DeleteImagePlacementAsync(Guid projectId, Guid placementId, CancellationToken cancellationToken = default);
    Task<PublishDocument> GetDocumentAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectExportFile> ExportAsync(Guid projectId, PublishExportFormat format, CancellationToken cancellationToken = default);
}

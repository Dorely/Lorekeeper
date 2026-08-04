using Lorekeeper.ImportExport;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public interface IPublishService
{
    Task<PublicationBookView> GetCoreWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublishWorkspaceView> GetWorkspaceAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublishDocument> GetCoreDocumentAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<PublishDocument> GetDocumentAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<ProjectExportFile> ExportCoreAsync(Guid projectId, PublishExportFormat format, CancellationToken cancellationToken = default);
    Task<ProjectExportFile> ExportAsync(Guid projectId, Guid editionId, PublishExportFormat format, CancellationToken cancellationToken = default);
}

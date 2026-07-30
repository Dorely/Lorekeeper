using Lorekeeper.ImportExport;
using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public interface IPublishService
{
    Task<PublishWorkspaceView> GetWorkspaceAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<PublishDocument> GetDocumentAsync(Guid projectId, Guid editionId, CancellationToken cancellationToken = default);
    Task<ProjectExportFile> ExportAsync(Guid projectId, Guid editionId, PublishExportFormat format, CancellationToken cancellationToken = default);
}

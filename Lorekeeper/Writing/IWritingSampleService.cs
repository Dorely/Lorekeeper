using Lorekeeper.Models;

namespace Lorekeeper.Writing;

public interface IWritingSampleService
{
    Task<IReadOnlyList<WritingSample>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<WritingSample?> GetAsync(Guid sampleId, CancellationToken cancellationToken = default);
    Task<WritingSample> CreateAsync(Guid projectId, string? title = null, string? body = null, CancellationToken cancellationToken = default);
    Task<WritingSample> UpdateAsync(Guid sampleId, string? title = null, string? body = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sampleId, CancellationToken cancellationToken = default);
}
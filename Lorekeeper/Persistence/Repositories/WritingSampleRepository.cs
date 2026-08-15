using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class WritingSampleRepository(AppDatabaseReadOperation operation) : IWritingSampleRepository
{
    public Task<List<WritingSample>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WritingSamples.Where(sample => sample.ProjectId == projectId)
                         .OrderByDescending(sample => sample.UpdatedAt)
                         .ThenBy(sample => sample.Title)
                         .ToListAsync(cancellationToken);

    public Task<WritingSample?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        operation.Db.WritingSamples.FirstOrDefaultAsync(sample => sample.Id == id, cancellationToken);

    public Task<int> CountByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        operation.Db.WritingSamples.CountAsync(sample => sample.ProjectId == projectId, cancellationToken);

    public async Task AddAsync(WritingSample sample, CancellationToken cancellationToken = default) =>
        await operation.Db.WritingSamples.AddAsync(sample, cancellationToken);

    public void Update(WritingSample sample) => operation.Db.MarkModified(sample);

    public void Remove(WritingSample sample) => operation.Db.MarkDeleted(sample);
}

using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public class WritingSampleRepository(AppDbContext db) : IWritingSampleRepository
{
    public Task<List<WritingSample>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.WritingSamples.Where(sample => sample.ProjectId == projectId)
                         .OrderByDescending(sample => sample.UpdatedAt)
                         .ThenBy(sample => sample.Title)
                         .ToListAsync(cancellationToken);

    public Task<WritingSample?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.WritingSamples.FirstOrDefaultAsync(sample => sample.Id == id, cancellationToken);

    public Task<int> CountByProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        db.WritingSamples.CountAsync(sample => sample.ProjectId == projectId, cancellationToken);

    public async Task AddAsync(WritingSample sample, CancellationToken cancellationToken = default) =>
        await db.WritingSamples.AddAsync(sample, cancellationToken);

    public void Update(WritingSample sample) => db.WritingSamples.Update(sample);

    public void Remove(WritingSample sample) => db.WritingSamples.Remove(sample);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
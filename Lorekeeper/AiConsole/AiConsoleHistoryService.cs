using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.AiConsole;

public sealed class AiConsoleHistoryService(AppDbContext db) : IAiConsoleHistoryService
{
    public async Task<IReadOnlyList<AiConsoleEntry>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await db.AiConsoleEntries
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(cancellationToken);

    public Task<AiConsoleEntry?> GetAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        db.AiConsoleEntries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
}

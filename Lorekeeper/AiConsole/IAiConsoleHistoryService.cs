using Lorekeeper.Models;

namespace Lorekeeper.AiConsole;

public interface IAiConsoleHistoryService
{
    Task<IReadOnlyList<AiConsoleEntry>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<AiConsoleEntry?> GetAsync(Guid projectId, Guid entryId, CancellationToken cancellationToken = default);
}

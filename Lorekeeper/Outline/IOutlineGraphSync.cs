using Lorekeeper.Models;

namespace Lorekeeper.Outline;

public interface IOutlineGraphSync
{
    Task EnsureProjectAsync(Project project, CancellationToken cancellationToken = default);
    Task EnsureActAsync(Act act, CancellationToken cancellationToken = default);
    Task RemoveActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default);
    Task EnsureChapterAsync(Chapter chapter, CancellationToken cancellationToken = default);
    Task RemoveChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default);
    Task RepairProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}
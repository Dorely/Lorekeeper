using Lorekeeper.Models;

namespace Lorekeeper.Projects;

public interface IProjectService
{
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task<Project> RenameAsync(Guid id, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the project and cascades to all child graph nodes (and their edges, transitively),
    /// and to all vector chunks stored under the project's scope key.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

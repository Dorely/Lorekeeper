using Lorekeeper.Models;

namespace Lorekeeper.Projects;

public interface IProjectService
{
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task<Project> RenameAsync(Guid id, string newName, CancellationToken cancellationToken = default);

    /// <summary>Replace the optional user-authored Project Guidance. Blank clears it.</summary>
    Task<Project> UpdateProjectGuidanceAsync(Guid id, string projectGuidance, CancellationToken cancellationToken = default);

    /// <summary>Toggle whether the currently-open chapter is included in the assembled context.</summary>
    Task<Project> SetIncludeCurrentChapterAsync(Guid id, bool include, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the project and cascades to all child graph nodes (and their edges, transitively),
    /// and to all vector chunks stored under the project's scope key.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the project. A project with incoming references can only be deleted when
    /// <paramref name="detachIncomingReferences"/> is explicitly true; the recheck and
    /// detachment occur in the same write operation as the delete.
    /// </summary>
    Task DeleteAsync(
        Guid id,
        bool detachIncomingReferences,
        CancellationToken cancellationToken = default);
}

using Lorekeeper.Models;

namespace Lorekeeper.Projects;

public interface IProjectService
{
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default);
    Task<Project?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<Project> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task<Project> RenameAsync(Guid id, string newName, CancellationToken cancellationToken = default);

    /// <summary>Replace the project's system prompt. Empty/whitespace is rejected.</summary>
    Task<Project> UpdateSystemPromptAsync(Guid id, string systemPrompt, CancellationToken cancellationToken = default);

    /// <summary>Toggle whether the currently-open chapter is included in the assembled context.</summary>
    Task<Project> SetIncludeCurrentChapterAsync(Guid id, bool include, CancellationToken cancellationToken = default);

    /// <summary>Toggle whether AI tool mutations are queued for user approval.</summary>
    Task<Project> SetAiChangeApprovalAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces (or merges, when <paramref name="merge"/> is true) the project's free-form
    /// <see cref="Project.Metadata"/> bag. Used by the Outline tab to persist wizard inputs.
    /// </summary>
    Task<Project> UpdateMetadataAsync(Guid id, IDictionary<string, object?> metadata, bool merge = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the project and cascades to all child graph nodes (and their edges, transitively),
    /// and to all vector chunks stored under the project's scope key.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

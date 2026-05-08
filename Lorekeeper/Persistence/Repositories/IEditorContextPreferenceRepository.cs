using Lorekeeper.Models;

namespace Lorekeeper.Persistence.Repositories;

public interface IEditorContextPreferenceRepository
{
    Task<List<EditorContextPreference>> ListForChapterAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default);

    Task<EditorContextPreference?> FindAsync(
        Guid projectId,
        Guid chapterId,
        string kind,
        string key,
        CancellationToken cancellationToken = default);

    Task AddAsync(EditorContextPreference preference, CancellationToken cancellationToken = default);
    void Update(EditorContextPreference preference);
    void Remove(EditorContextPreference preference);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
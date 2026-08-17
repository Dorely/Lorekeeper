using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EditorContextPreferenceRepository(AppDatabaseReadOperation operation) : IEditorContextPreferenceRepository
{
    public Task<List<EditorContextPreference>> ListForChapterAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        operation.Db.EditorContextPreferences
            .AsNoTracking()
            .Where(preference => preference.ProjectId == projectId && preference.ChapterId == chapterId)
            .OrderBy(preference => preference.SortOrder ?? int.MaxValue)
            .ThenBy(preference => preference.Kind)
            .ThenBy(preference => preference.Key)
            .ToListAsync(cancellationToken);

    public Task<EditorContextPreference?> FindAsync(
        Guid projectId,
        Guid chapterId,
        string kind,
        string key,
        CancellationToken cancellationToken = default) =>
        operation.Db.EditorContextPreferences.FirstOrDefaultAsync(
            preference => preference.ProjectId == projectId
                && preference.ChapterId == chapterId
                && preference.Kind == kind
                && preference.Key == key,
            cancellationToken);

    public async Task AddAsync(EditorContextPreference preference, CancellationToken cancellationToken = default) =>
        await operation.Db.EditorContextPreferences.AddAsync(preference, cancellationToken);

    public async Task<int> RemoveForChapterAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var preferences = await operation.Db.EditorContextPreferences
            .Where(preference => preference.ProjectId == projectId && preference.ChapterId == chapterId)
            .ToListAsync(cancellationToken);
        operation.Db.EditorContextPreferences.RemoveRange(preferences);
        return preferences.Count;
    }

    public void Update(EditorContextPreference preference) => operation.Db.MarkModified(preference);
}

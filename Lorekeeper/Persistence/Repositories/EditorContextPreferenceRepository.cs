using Lorekeeper.Models;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Persistence.Repositories;

public sealed class EditorContextPreferenceRepository(AppDbContext db) : IEditorContextPreferenceRepository
{
    public Task<List<EditorContextPreference>> ListForChapterAsync(
        Guid projectId,
        Guid chapterId,
        CancellationToken cancellationToken = default) =>
        db.EditorContextPreferences
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
        db.EditorContextPreferences.FirstOrDefaultAsync(
            preference => preference.ProjectId == projectId
                && preference.ChapterId == chapterId
                && preference.Kind == kind
                && preference.Key == key,
            cancellationToken);

    public async Task AddAsync(EditorContextPreference preference, CancellationToken cancellationToken = default) =>
        await db.EditorContextPreferences.AddAsync(preference, cancellationToken);

    public void Update(EditorContextPreference preference) => db.EditorContextPreferences.Update(preference);

    public void Remove(EditorContextPreference preference) => db.EditorContextPreferences.Remove(preference);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}

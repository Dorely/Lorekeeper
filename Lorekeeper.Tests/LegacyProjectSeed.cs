using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Tests;

/// <summary>
/// Seeds projects into schemas that predate the Review Edits column rename.
/// Historical migration tests intentionally use the legacy column here rather
/// than asking the current EF model to write a column that does not exist yet.
/// </summary>
internal static class LegacyProjectSeed
{
    public static Task InsertAsync(
        AppDbContext db,
        Guid projectId,
        string name,
        string slug,
        string projectGuidance = "") =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Projects
                (Id, AiChangeApprovalEnabled, ContestModeEnabled, CreatedAt,
                 IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
            VALUES
                ({projectId}, 1, 0, {DateTime.UtcNow}, 1, {name}, {projectGuidance}, {slug}, {DateTime.UtcNow});
            """);
}

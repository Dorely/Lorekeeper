using Lorekeeper.ChapterVisuals;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Lorekeeper.Publish;

public static class PublishEndpoints
{
    public static IEndpointRouteBuilder MapPublishEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/cover/{chapterId:guid}/preview",
            async (
                Guid projectId,
                Guid chapterId,
                [FromQuery] int? maxEdge,
                AppDbContext db,
                IChapterVisualService chapterVisuals,
                CancellationToken cancellationToken) =>
            {
                var chapter = await db.Chapters
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        candidate => candidate.ProjectId == projectId
                            && candidate.Id == chapterId
                            && candidate.VisualMode == ChapterVisualMode.PicturePage,
                        cancellationToken);
                if (chapter is null)
                    return Results.NotFound();

                var edge = Math.Clamp(maxEdge ?? 1200, 320, 1600);
                var snapshots = await chapterVisuals.RenderSnapshotsAsync(
                    chapterId,
                    edge,
                    includeGuides: false,
                    cancellationToken);
                var preview = snapshots.FirstOrDefault();
                if (preview is null)
                    return Results.NotFound();

                var etag = new EntityTagHeaderValue($"\"{chapter.Id:N}-{chapter.UpdatedAt.Ticks:x}-{edge:x}\"");
                return Results.File(
                    preview.Data,
                    preview.ContentType,
                    fileDownloadName: null,
                    lastModified: chapter.UpdatedAt,
                    entityTag: etag,
                    enableRangeProcessing: true);
            });

        return endpoints;
    }
}

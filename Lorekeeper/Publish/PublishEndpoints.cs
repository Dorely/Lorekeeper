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

        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/picture-pages/{chapterId:guid}/surface",
            async (
                Guid projectId,
                Guid chapterId,
                [FromQuery] int? physicalPageLongEdge,
                [FromQuery] ChapterPicturePageSurfaceRotation? rotation,
                HttpContext httpContext,
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

                var surfaceRotation = rotation ?? ChapterPicturePageSurfaceRotation.None;
                if (!Enum.IsDefined(surfaceRotation))
                    return Results.BadRequest("The Picture Page surface rotation is invalid.");

                var edge = Math.Clamp(physicalPageLongEdge ?? 2400, 320, 2400);
                var etag = new EntityTagHeaderValue(
                    $"\"picture-page-{chapter.Id:N}-{chapter.UpdatedAt.Ticks:x}-{edge:x}-{(int)surfaceRotation:x}\"");
                var responseHeaders = httpContext.Response.GetTypedHeaders();
                responseHeaders.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                };
                responseHeaders.ETag = etag;
                if (MatchesIfNoneMatch(httpContext.Request, etag))
                    return Results.StatusCode(StatusCodes.Status304NotModified);

                var surfaces = await chapterVisuals.RenderPicturePageSurfacesAsync(
                    [chapterId],
                    edge,
                    surfaceRotation,
                    cancellationToken);
                if (!surfaces.TryGetValue(chapterId, out var surface))
                    return Results.NotFound();

                return Results.File(
                    surface.Data,
                    surface.ContentType,
                    fileDownloadName: null,
                    lastModified: chapter.UpdatedAt,
                    entityTag: etag,
                    enableRangeProcessing: true);
            });

        return endpoints;
    }

    private static bool MatchesIfNoneMatch(HttpRequest request, EntityTagHeaderValue etag)
    {
        var expected = etag.ToString();
        return request.Headers.IfNoneMatch
            .SelectMany(value => (value ?? string.Empty).Split(','))
            .Select(value => value.Trim())
            .Any(value => value == "*"
                || string.Equals(value, expected, StringComparison.Ordinal)
                || value.StartsWith("W/", StringComparison.Ordinal)
                    && string.Equals(value[2..], expected, StringComparison.Ordinal));
    }
}

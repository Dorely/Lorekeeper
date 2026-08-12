using Lorekeeper.Persistence;
using Lorekeeper.EntityVisuals;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Images;

public static class ProjectImageEndpoints
{
    public static IEndpointRouteBuilder MapProjectImages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/projects/{projectId:guid}/images/{imageId:guid}/content",
            async (
                Guid projectId,
                Guid imageId,
                [FromQuery] int? maxEdge,
                IProjectImageService images,
                CancellationToken cancellationToken) =>
            {
                var image = await images.GetDataAsync(projectId, imageId, maxEdge, cancellationToken);
                if (image is null)
                    return Results.NotFound();

                var etag = $"\"{image.Id:N}-{image.UpdatedAt.Ticks:x}-{image.Data.LongLength:x}\"";
                return Results.File(
                    image.Data,
                    image.ContentType,
                    fileDownloadName: null,
                    lastModified: image.UpdatedAt,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(etag),
                    enableRangeProcessing: true);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/image-masks/{maskId:guid}/content",
            async (
                Guid projectId,
                Guid maskId,
                IProjectImageJobService imageJobs,
                CancellationToken cancellationToken) =>
            {
                var mask = await imageJobs.GetMaskDataAsync(projectId, maskId, cancellationToken);
                if (mask is null)
                    return Results.NotFound();

                var etag = $"\"{mask.Id:N}-{mask.UpdatedAt.Ticks:x}-{mask.Data.LongLength:x}\"";
                return Results.File(
                    mask.Data,
                    mask.ContentType,
                    fileDownloadName: null,
                    lastModified: mask.UpdatedAt,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(etag),
                    enableRangeProcessing: true);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/source-visuals/{candidateId:guid}/content",
            async (
                Guid projectId,
                Guid candidateId,
                [FromQuery] int? maxEdge,
                IEntityVisualExampleService visuals,
                CancellationToken cancellationToken) =>
            {
                var candidate = await visuals.GetCandidateDataAsync(projectId, candidateId, maxEdge, cancellationToken);
                return candidate is null
                    ? Results.NotFound()
                    : Results.File(candidate.Data, candidate.ContentType, enableRangeProcessing: true);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/editor-chat-visuals/{visualId:guid}/content",
            async (
                Guid projectId,
                Guid visualId,
                [FromQuery] int? maxEdge,
                IProjectImageService images,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var visual = await db.EditorMessageVisuals
                    .AsNoTracking()
                    .Include(item => item.Message)
                    .ThenInclude(message => message.Conversation)
                    .FirstOrDefaultAsync(item => item.Id == visualId && item.Message.Conversation.ProjectId == projectId, cancellationToken);
                if (visual is null)
                    return Results.NotFound();

                if (string.Equals(visual.SourceKind, "projectImage", StringComparison.Ordinal)
                    && visual.SourceRefId is { } imageId)
                {
                    var image = await images.GetDataAsync(projectId, imageId, maxEdge, cancellationToken);
                    if (image is null)
                        return Results.NotFound();

                    return Results.File(
                        image.Data,
                        image.ContentType,
                        fileDownloadName: null,
                        lastModified: image.UpdatedAt,
                        enableRangeProcessing: true);
                }

                if (visual.Data is not { Length: > 0 })
                    return Results.NotFound();

                var visualData = maxEdge is int edge && edge > 0
                    ? ProjectImageResize.Resize(visual.Data, visual.ContentType, edge)
                    : visual.Data;
                return Results.File(
                    visualData,
                    visual.ContentType,
                    fileDownloadName: null,
                    lastModified: visual.CreatedAt,
                    enableRangeProcessing: true);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/image-chat-visuals/{visualId:guid}/content",
            async (
                Guid projectId,
                Guid visualId,
                [FromQuery] int? maxEdge,
                IProjectImageService images,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var visual = await db.ProjectImageMessageVisuals
                    .AsNoTracking()
                    .Include(item => item.Message)
                    .ThenInclude(message => message.Conversation)
                    .FirstOrDefaultAsync(item => item.Id == visualId && item.Message.Conversation.ProjectId == projectId, cancellationToken);
                if (visual is null)
                    return Results.NotFound();

                if (string.Equals(visual.SourceKind, "projectImage", StringComparison.Ordinal)
                    && visual.SourceRefId is { } imageId)
                {
                    var image = await images.GetDataAsync(projectId, imageId, maxEdge, cancellationToken);
                    if (image is null)
                        return Results.NotFound();

                    return Results.File(
                        image.Data,
                        image.ContentType,
                        fileDownloadName: null,
                        lastModified: image.UpdatedAt,
                        enableRangeProcessing: true);
                }

                if (visual.Data is not { Length: > 0 })
                    return Results.NotFound();

                var visualData = maxEdge is int edge && edge > 0
                    ? ProjectImageResize.Resize(visual.Data, visual.ContentType, edge)
                    : visual.Data;
                return Results.File(
                    visualData,
                    visual.ContentType,
                    fileDownloadName: null,
                    lastModified: visual.CreatedAt,
                    enableRangeProcessing: true);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/publish-chat-visuals/{visualId:guid}/content",
            async (
                Guid projectId,
                Guid visualId,
                [FromQuery] int? maxEdge,
                AppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var visual = await db.PublishMessageVisuals
                    .AsNoTracking()
                    .Include(item => item.Message)
                    .ThenInclude(message => message.Conversation)
                    .FirstOrDefaultAsync(item => item.Id == visualId && item.Message.Conversation.ProjectId == projectId, cancellationToken);
                if (visual is null || visual.Data.Length == 0)
                    return Results.NotFound();

                var visualData = maxEdge is int edge && edge > 0
                    ? ProjectImageResize.Resize(visual.Data, visual.ContentType, edge)
                    : visual.Data;
                return Results.File(
                    visualData,
                    visual.ContentType,
                    fileDownloadName: null,
                    lastModified: visual.CreatedAt,
                    enableRangeProcessing: true);
            });

        return endpoints;
    }
}

using Microsoft.AspNetCore.Mvc;

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

        return endpoints;
    }
}

using Microsoft.AspNetCore.Mvc;

namespace Lorekeeper.Fonts;

public static class ProjectFontEndpoints
{
    public static IEndpointRouteBuilder MapProjectFonts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/projects/{projectId:guid}/fonts/{faceId:guid}/content",
            async (
                Guid projectId,
                Guid faceId,
                IProjectFontService fonts,
                CancellationToken cancellationToken) =>
            {
                var face = await fonts.GetFaceDataAsync(projectId, faceId, cancellationToken);
                if (face is null)
                    return Results.NotFound();

                var etag = $"\"{face.Id:N}-{face.Data.LongLength:x}\"";
                return Results.File(
                    face.Data,
                    face.ContentType,
                    fileDownloadName: null,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(etag),
                    enableRangeProcessing: true);
            });
        return endpoints;
    }
}


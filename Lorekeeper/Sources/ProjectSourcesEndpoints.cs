using Microsoft.Net.Http.Headers;

namespace Lorekeeper.Sources;

public static class ProjectSourcesEndpoints
{
    public static IEndpointRouteBuilder MapProjectSources(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/projects/{projectId:guid}/sources/{sourceId:guid}/original",
            async (
                Guid projectId,
                Guid sourceId,
                HttpResponse response,
                IProjectSourcesService sources,
                CancellationToken cancellationToken) =>
            {
                var download = await sources.GetOriginalDownloadAsync(projectId, sourceId, cancellationToken);
                if (download is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                response.ContentType = download.MediaType;
                response.ContentLength = download.Length;
                response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileNameStar = download.FileName,
                }.ToString();
                response.Headers[HeaderNames.ETag] = $"\"{download.Sha256}\"";
                response.Headers[HeaderNames.AcceptRanges] = "none";
                await sources.CopyOriginalAsync(download, response.Body, cancellationToken);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/sources/{sourceId:guid}/pdf-pages/{pageNumber:int}.png",
            async (
                Guid projectId,
                Guid sourceId,
                int pageNumber,
                int? maxEdge,
                IProjectSourcesService sources,
                CancellationToken cancellationToken) =>
            {
                var page = await sources.RenderPdfPageAsync(projectId, sourceId, pageNumber, maxEdge ?? 1_280, cancellationToken);
                return page is null
                    ? Results.NotFound()
                    : Results.File(page.Data, "image/png", enableRangeProcessing: false);
            });

        return endpoints;
    }
}

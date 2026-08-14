using Microsoft.Net.Http.Headers;

namespace Lorekeeper.Publish;

public static class PublishEndpoints
{
    public static IEndpointRouteBuilder MapPublishEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/releases/{editionId:guid}/exports/{format}",
            async (
                Guid projectId,
                Guid editionId,
                string format,
                IPublishService publishing,
                CancellationToken cancellationToken) =>
            {
                if (!Enum.TryParse<PublishExportFormat>(format, ignoreCase: true, out var exportFormat)
                    || !Enum.IsDefined(exportFormat))
                {
                    return Results.BadRequest("The publication export format is invalid.");
                }

                var file = await publishing.ExportAsync(projectId, editionId, exportFormat, cancellationToken);
                return Results.File(file.Content, file.ContentType, file.FileName);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/artifacts/{artifactId:guid}",
            async (
                Guid projectId,
                Guid artifactId,
                IPublicationRenderService renders,
                CancellationToken cancellationToken) =>
            {
                var artifact = await renders.GetArtifactAsync(projectId, artifactId, cancellationToken);
                if (artifact is null)
                    return Results.NotFound();
                var etag = new EntityTagHeaderValue($"\"sha256-{artifact.Sha256}\"");
                return Results.File(
                    artifact.Data,
                    artifact.MediaType,
                    fileDownloadName: null,
                    lastModified: artifact.CreatedAt,
                    entityTag: etag,
                    enableRangeProcessing: true);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/artifacts/{artifactId:guid}/download",
            async (
                Guid projectId,
                Guid artifactId,
                IPublicationRenderService renders,
                CancellationToken cancellationToken) =>
            {
                var artifact = await renders.GetArtifactAsync(projectId, artifactId, cancellationToken);
                return artifact is null
                    ? Results.NotFound()
                    : Results.File(artifact.Data, artifact.MediaType, artifact.FileName);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/artifacts/{artifactId:guid}/pages/{pageNumber:int}.png",
            async (
                Guid projectId,
                Guid artifactId,
                int pageNumber,
                int? width,
                HttpContext httpContext,
                IPublicationArtifactPreviewService previews,
                CancellationToken cancellationToken) =>
            {
                var page = await previews.RenderPageAsync(
                    projectId,
                    artifactId,
                    pageNumber,
                    width ?? 1200,
                    cancellationToken);
                if (page is null)
                    return Results.NotFound();

                httpContext.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                var etag = new EntityTagHeaderValue(
                    $"\"sha256-{page.ArtifactSha256}-page-{page.PageNumber}-w-{page.Width}\"");
                return Results.File(
                    page.Data,
                    "image/png",
                    fileDownloadName: null,
                    lastModified: page.CreatedAt,
                    entityTag: etag);
            });

        endpoints.MapGet(
            "/projects/{projectId:guid}/publish/artifacts/{artifactId:guid}/epub/{**resourcePath}",
            async (
                Guid projectId,
                Guid artifactId,
                string resourcePath,
                HttpContext httpContext,
                IPublicationEpubPreviewService previews,
                CancellationToken cancellationToken) =>
            {
                PublicationEpubPreviewResource? resource;
                try
                {
                    resource = await previews.ReadResourceAsync(
                        projectId,
                        artifactId,
                        resourcePath,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException or UriFormatException)
                {
                    return Results.BadRequest("The EPUB artifact could not be previewed safely.");
                }

                if (resource is null)
                    return Results.NotFound();

                httpContext.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                httpContext.Response.Headers.ContentSecurityPolicy =
                    "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self'; " +
                    "script-src 'none'; connect-src 'none'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";
                httpContext.Response.Headers.XContentTypeOptions = "nosniff";
                httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
                httpContext.Response.Headers.AccessControlAllowOrigin = "*";
                var etag = new EntityTagHeaderValue(
                    $"\"sha256-{resource.ArtifactSha256}-{resource.ResourceSha256}\"");
                var responseMediaType = resource.MediaType.Equals(
                    "application/xhtml+xml",
                    StringComparison.OrdinalIgnoreCase)
                        ? "text/html; charset=utf-8"
                        : resource.MediaType;
                return Results.File(
                    resource.Data,
                    responseMediaType,
                    fileDownloadName: null,
                    lastModified: resource.CreatedAt,
                    entityTag: etag);
            });

        return endpoints;
    }
}

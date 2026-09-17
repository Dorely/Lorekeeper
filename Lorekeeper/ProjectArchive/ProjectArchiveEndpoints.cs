using Microsoft.AspNetCore.Mvc;

namespace Lorekeeper.ProjectArchive;

/// <summary>HTTP-only streaming handoff for portable archive downloads.</summary>
public static class ProjectArchiveEndpoints
{
    public static IEndpointRouteBuilder MapProjectArchiveEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/projects/{projectId:guid}/archive",
            async (
                Guid projectId,
                [FromQuery] string? policy,
                HttpResponse response,
                IProjectArchiveService archives,
                CancellationToken cancellationToken) =>
            {
                var selected = policy?.Trim() switch
                {
                    nameof(ProjectDependencyTraversalPolicy.FullArchive) => ProjectDependencyTraversalPolicy.FullArchive,
                    nameof(ProjectDependencyTraversalPolicy.NonStructuralArchive) => ProjectDependencyTraversalPolicy.NonStructuralArchive,
                    _ => throw new BadHttpRequestException("An archive policy is required."),
                };
                response.ContentType = "application/vnd.lorekeeper.archive+zip";
                response.Headers.ContentDisposition = $"attachment; filename=\"{projectId:D}.lorekeeper\"";
                response.Headers.CacheControl = "no-store";
                await archives.WriteAsync(projectId, selected, response.Body, cancellationToken);
            });
        return endpoints;
    }
}

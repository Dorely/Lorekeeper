namespace Lorekeeper.ProjectArchive;

public sealed record ProjectArchiveWriteResult(
    string FileName,
    string ContentType,
    ProjectArchiveManifest Manifest);

public interface IProjectArchiveService
{
    Task<ProjectArchiveWriteResult> WriteAsync(
        Guid projectId,
        ProjectDependencyTraversalPolicy policy,
        Stream destination,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectArchiveService(IProjectDependencyTraversalService traversal, ProjectArchiveLimits? limits = null) : IProjectArchiveService
{
    public async Task<ProjectArchiveWriteResult> WriteAsync(
        Guid projectId,
        ProjectDependencyTraversalPolicy policy,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var capture = await traversal.CaptureAsync(projectId, policy, cancellationToken);
        var manifest = await ProjectArchiveZip.WriteAsync(
            destination,
            projectId.ToString("D"),
            capture.SchemaVersions,
            policy,
            capture.Files,
            capture.Warnings,
            limits,
            cancellationToken: cancellationToken);
        return new ProjectArchiveWriteResult(
            $"{projectId:D}.lorekeeper",
            "application/vnd.lorekeeper.archive+zip",
            manifest);
    }
}

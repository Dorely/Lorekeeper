using Lorekeeper.Models;
using Lorekeeper.Context;
using Lorekeeper.Persistence;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Writing;

public sealed class WritingSampleService(
IAppDatabaseOperationFactory database,
IContextIndexingService contextIndexing) : IWritingSampleService
{
    public async Task<IReadOnlyList<WritingSample>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var samples = databaseOperation.Repositories.WritingSamples;
        return await samples.ListByProjectAsync(projectId, cancellationToken);
    }
    public async Task<WritingSample?> GetAsync(Guid sampleId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenReadAsync(cancellationToken);
        var samples = databaseOperation.Repositories.WritingSamples;
        return await samples.GetByIdAsync(sampleId, cancellationToken);
    }
    public async Task<WritingSample> CreateAsync(Guid projectId, string? title = null, string? body = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var samples = databaseOperation.Repositories.WritingSamples;
        var projects = databaseOperation.Repositories.Projects;
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} not found.");

        var count = await samples.CountByProjectAsync(projectId, cancellationToken);
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? $"Writing Sample {count + 1}"
            : title.Trim();

        if (resolvedTitle.Length == 0)
            throw new ArgumentException("Writing sample title is required.", nameof(title));

        var sample = new WritingSample
        {
            ProjectId = projectId,
            Title = resolvedTitle,
            Body = body ?? string.Empty,
        };

        await samples.AddAsync(sample, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        projects.Update(project);
        await databaseOperation.SaveChangesAsync(cancellationToken);
        await contextIndexing.ReindexWritingSampleAsync(sample.Id, cancellationToken);
        return sample;
    }

    public async Task<WritingSample> UpdateAsync(Guid sampleId, string? title = null, string? body = null, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var samples = databaseOperation.Repositories.WritingSamples;
        var projects = databaseOperation.Repositories.Projects;
        var sample = await samples.GetByIdAsync(sampleId, cancellationToken)
            ?? throw new InvalidOperationException($"Writing sample {sampleId} not found.");

        var changed = false;
        if (title is not null)
        {
            var trimmed = title.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("Writing sample title is required.", nameof(title));
            if (trimmed != sample.Title)
            {
                sample.Title = trimmed;
                changed = true;
            }
        }

        if (body is not null && body != sample.Body)
        {
            sample.Body = body;
            changed = true;
        }

        if (!changed) return sample;

        sample.UpdatedAt = DateTime.UtcNow;
        samples.Update(sample);

        var project = await projects.GetByIdAsync(sample.ProjectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await contextIndexing.ReindexWritingSampleAsync(sample.Id, cancellationToken);
        return sample;
    }

    public async Task DeleteAsync(Guid sampleId, CancellationToken cancellationToken = default)
    {
        await using var databaseOperation = await database.OpenWriteAsync(cancellationToken);
        databaseOperation.ShareWithNestedOperations();
        var samples = databaseOperation.Repositories.WritingSamples;
        var projects = databaseOperation.Repositories.Projects;
        var sample = await samples.GetByIdAsync(sampleId, cancellationToken);
        if (sample is null) return;

        var projectId = sample.ProjectId;
        samples.Remove(sample);

        var project = await projects.GetByIdAsync(projectId, cancellationToken);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
            projects.Update(project);
        }

        await databaseOperation.SaveChangesAsync(cancellationToken);
        await contextIndexing.DeleteWritingSampleAsync(projectId, sampleId, cancellationToken);
    }
}

using Lorekeeper.Models;
using Lorekeeper.Persistence.Repositories;

namespace Lorekeeper.Writing;

public sealed class WritingSampleService(
    IWritingSampleRepository samples,
    IProjectRepository projects) : IWritingSampleService
{
    public async Task<IReadOnlyList<WritingSample>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await samples.ListByProjectAsync(projectId, cancellationToken);

    public Task<WritingSample?> GetAsync(Guid sampleId, CancellationToken cancellationToken = default) =>
        samples.GetByIdAsync(sampleId, cancellationToken);

    public async Task<WritingSample> CreateAsync(Guid projectId, string? title = null, string? body = null, CancellationToken cancellationToken = default)
    {
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
        await samples.SaveChangesAsync(cancellationToken);
        return sample;
    }

    public async Task<WritingSample> UpdateAsync(Guid sampleId, string? title = null, string? body = null, CancellationToken cancellationToken = default)
    {
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

        await samples.SaveChangesAsync(cancellationToken);
        return sample;
    }

    public async Task DeleteAsync(Guid sampleId, CancellationToken cancellationToken = default)
    {
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

        await samples.SaveChangesAsync(cancellationToken);
    }
}
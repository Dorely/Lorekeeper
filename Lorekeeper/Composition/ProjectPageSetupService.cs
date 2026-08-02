using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lorekeeper.Composition;

public interface IProjectPageSetupService
{
    Task<ProjectPageSetup> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectPageSetup> UpdateAsync(Guid projectId, long expectedRevision, ProjectPageSetupInput input, CancellationToken cancellationToken = default);
}

public sealed record ProjectPageSetupInput(
    double PageWidthInches,
    double PageHeightInches,
    double PageMarginInches,
    double BodyFontSizePoints,
    double BodyLineHeight);

public sealed class ProjectPageSetupService(
    AppDbContext db,
    IProjectMutationCoordinator projectMutations) : IProjectPageSetupService
{
    public async Task<ProjectPageSetup> GetOrCreateAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var existing = await db.ProjectPageSetups.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        if (existing is not null)
            return existing;

        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        existing = await db.ProjectPageSetups.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
        if (existing is not null)
            return existing;
        if (!await db.Projects.AnyAsync(item => item.Id == projectId, cancellationToken))
            throw new KeyNotFoundException("Project was not found.");

        var setup = new ProjectPageSetup { ProjectId = projectId };
        db.ProjectPageSetups.Add(setup);
        await db.SaveChangesAsync(cancellationToken);
        return setup;
    }

    public async Task<ProjectPageSetup> UpdateAsync(
        Guid projectId,
        long expectedRevision,
        ProjectPageSetupInput input,
        CancellationToken cancellationToken = default)
    {
        Validate(input);
        await using var mutation = await projectMutations.AcquireAsync(projectId, cancellationToken);
        var setup = await db.ProjectPageSetups.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Project page setup was not found.");
        if (setup.Revision != expectedRevision)
            throw new InvalidOperationException($"Page setup revision conflict: expected {expectedRevision}, current revision is {setup.Revision}.");

        setup.PageWidthInches = input.PageWidthInches;
        setup.PageHeightInches = input.PageHeightInches;
        setup.PageMarginInches = input.PageMarginInches;
        setup.BodyFontSizePoints = input.BodyFontSizePoints;
        setup.BodyLineHeight = input.BodyLineHeight;
        setup.Revision = checked(setup.Revision + 1);
        setup.UpdatedAt = DateTime.UtcNow;
        var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return setup;
    }

    private static void Validate(ProjectPageSetupInput input)
    {
        if (input.PageWidthInches is < 3 or > 24 || input.PageHeightInches is < 3 or > 24)
            throw new ArgumentOutOfRangeException(nameof(input), "Page dimensions must be between 3 and 24 inches.");
        if (input.PageMarginInches is < 0.25 or > 3
            || input.PageMarginInches * 2 >= Math.Min(input.PageWidthInches, input.PageHeightInches))
            throw new ArgumentOutOfRangeException(nameof(input), "Page margins do not leave a usable content area.");
        if (input.BodyFontSizePoints is < 6 or > 36 || input.BodyLineHeight is < 0.8 or > 3)
            throw new ArgumentOutOfRangeException(nameof(input), "Body typography is outside the supported range.");
    }
}

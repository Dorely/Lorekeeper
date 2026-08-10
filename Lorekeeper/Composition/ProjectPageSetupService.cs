using System.Text.Json;
using Lorekeeper.Manuscripts;
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var setup = await db.ProjectPageSetups.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Project page setup was not found.");
        if (setup.Revision != expectedRevision)
            throw new InvalidOperationException($"Page setup revision conflict: expected {expectedRevision}, current revision is {setup.Revision}.");

        var geometryChanged = setup.PageWidthInches != input.PageWidthInches
            || setup.PageHeightInches != input.PageHeightInches;
        setup.PageWidthInches = input.PageWidthInches;
        setup.PageHeightInches = input.PageHeightInches;
        setup.PageMarginInches = input.PageMarginInches;
        setup.BodyFontSizePoints = input.BodyFontSizePoints;
        setup.BodyLineHeight = input.BodyLineHeight;
        setup.Revision = checked(setup.Revision + 1);
        setup.UpdatedAt = DateTime.UtcNow;
        var project = await db.Projects.SingleAsync(item => item.Id == projectId, cancellationToken);
        project.UpdatedAt = DateTime.UtcNow;
        if (geometryChanged)
            await ReflowCoreCoverAsync(projectId, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return setup;
    }

    private async Task ReflowCoreCoverAsync(
        Guid projectId,
        ProjectPageSetupInput input,
        CancellationToken cancellationToken)
    {
        var book = await db.PublicationBooks.SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        var cover = await db.PublicationBookCoverDesigns.SingleOrDefaultAsync(
            item => item.ProjectId == projectId,
            cancellationToken);
        if (book is null || cover is null || string.IsNullOrWhiteSpace(cover.CompositionSceneJson))
            return;

        var scene = JsonSerializer.Deserialize<CompositionScene>(
            cover.CompositionSceneJson,
            ManuscriptCodec.JsonOptions) ?? throw new InvalidDataException("The Core cover composition is empty.");
        var edition = new PublicationEdition
        {
            ProjectId = projectId,
            Name = "Core Book",
            Format = PublicationEditionFormat.DigitalPdf,
            Binding = PublicationBinding.Digital,
            Paper = PublicationPaper.Digital,
            Ink = PublicationInk.Digital,
            PageWidthInches = input.PageWidthInches,
            PageHeightInches = input.PageHeightInches,
            PageMarginInches = input.PageMarginInches,
            BodyFontSizePoints = input.BodyFontSizePoints,
            BodyLineHeight = input.BodyLineHeight,
        };
        var reflowed = CoverCompositionFactory.Reflow(
            edition,
            new PublicationCoverDesign { EditionId = Guid.Empty },
            scene,
            0,
            0);
        cover.CompositionSceneJson = JsonSerializer.Serialize(
            reflowed,
            ManuscriptCodec.JsonOptions);
        cover.Revision = checked(cover.Revision + 1);
        cover.UpdatedAt = DateTime.UtcNow;
        book.Revision = checked(book.Revision + 1);
        book.UpdatedAt = DateTime.UtcNow;
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

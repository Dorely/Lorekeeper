using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ManuscriptStyleServiceTests
{
    [Fact]
    public async Task StylesAreRevisionCheckedAndSemanticKeysAreImmutable()
    {
        await using var fixture = await StyleFixture.CreateAsync();
        var service = new ManuscriptStyleService(fixture.Db, fixture.Mutations);
        var created = await service.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                null,
                "Body",
                ManuscriptStyleKind.Paragraph,
                "body",
                new ManuscriptStyleProperties(
                    FontFamilyKey: " serif ",
                    FontSizePoints: 11,
                    TextAlign: " LEFT ")));
        Assert.Equal("serif", created.Definition.FontFamilyKey);
        Assert.Equal("left", created.Definition.TextAlign);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertAsync(
                fixture.ProjectId,
                new ManuscriptStyleInput(
                    created.Id,
                    "Body",
                    ManuscriptStyleKind.Paragraph,
                    "body-renamed",
                    created.Definition,
                    created.Revision)));
        await Assert.ThrowsAsync<ManuscriptStyleConflictException>(() =>
            service.UpsertAsync(
                fixture.ProjectId,
                new ManuscriptStyleInput(
                    created.Id,
                    "Book body",
                    ManuscriptStyleKind.Paragraph,
                    "body",
                    created.Definition,
                    ExpectedRevision: 0)));
    }

    [Fact]
    public async Task NameOnlyEditKeepsUnspecifiedBooleanPropertiesCanonical()
    {
        await using var fixture = await StyleFixture.CreateAsync();
        var service = new ManuscriptStyleService(fixture.Db, fixture.Mutations);
        var created = await service.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                null,
                "Opening",
                ManuscriptStyleKind.Paragraph,
                "opening",
                new ManuscriptStyleProperties()));

        var renamed = await service.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                created.Id,
                "Opening renamed",
                created.Kind,
                created.SemanticRole,
                created.Definition with
                {
                    Italic = false,
                    SmallCaps = false,
                    KeepWithNext = false,
                },
                created.Revision));

        Assert.Null(renamed.Definition.Italic);
        Assert.Null(renamed.Definition.SmallCaps);
        Assert.Null(renamed.Definition.KeepWithNext);
        Assert.Equal(renamed.Definition, Assert.Single(await service.ListAsync(fixture.ProjectId)).Definition);
    }

    [Fact]
    public async Task NamesRemainCaseInsensitiveAndUsedStyleDeletionFailsClosed()
    {
        await using var fixture = await StyleFixture.CreateAsync();
        var service = new ManuscriptStyleService(fixture.Db, fixture.Mutations);
        var created = await service.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                null,
                "Body",
                ManuscriptStyleKind.Paragraph,
                "custom-body",
                new ManuscriptStyleProperties()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertAsync(
                fixture.ProjectId,
                new ManuscriptStyleInput(
                    null,
                    "BODY",
                    ManuscriptStyleKind.Paragraph,
                    "other-role",
                    new ManuscriptStyleProperties())));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertAsync(
                fixture.ProjectId,
                new ManuscriptStyleInput(
                    null,
                    "Different name",
                    ManuscriptStyleKind.Paragraph,
                    "CUSTOM-BODY",
                    new ManuscriptStyleProperties(FontSizePoints: 12))));

        var chapter = new Chapter
        {
            ProjectId = fixture.ProjectId,
            Title = "Chapter",
        };
        var manuscript = ManuscriptCodec.FromPlainText(chapter.Id, "Styled", revision: 1);
        manuscript.Content[0] = manuscript.Content[0] with { StyleRole = "custom-body" };
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(manuscript);
        chapter.ManuscriptRevision = manuscript.Revision;
        fixture.Db.Chapters.Add(chapter);
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(fixture.ProjectId, created.Id, created.Revision));
    }

    [Fact]
    public async Task BuiltInRoleOverrideCanBeDeletedWhileTheRoleRemainsInUse()
    {
        await using var fixture = await StyleFixture.CreateAsync();
        var service = new ManuscriptStyleService(fixture.Db, fixture.Mutations);
        var created = await service.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                null,
                "Body override",
                ManuscriptStyleKind.Paragraph,
                ManuscriptStyleRoles.Body,
                new ManuscriptStyleProperties(FontSizePoints: 11)));
        var chapter = new Chapter
        {
            ProjectId = fixture.ProjectId,
            Title = "Chapter",
        };
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(
            ManuscriptCodec.FromPlainText(chapter.Id, "Body", revision: 1));
        chapter.ManuscriptRevision = 1;
        fixture.Db.Chapters.Add(chapter);
        await fixture.Db.SaveChangesAsync();

        await service.DeleteAsync(fixture.ProjectId, created.Id, created.Revision);

        Assert.Empty(await service.ListAsync(fixture.ProjectId));
    }

    [Fact]
    public async Task ProjectMutationBoundaryMakesConcurrentStyleDeleteObserveTheNewReference()
    {
        await using var fixture = await StyleFixture.CreateAsync();
        var service = new ManuscriptStyleService(fixture.Db, fixture.Mutations);
        var created = await service.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                null,
                "Opening",
                ManuscriptStyleKind.Paragraph,
                "opening",
                new ManuscriptStyleProperties()));
        var lease = await fixture.Mutations.AcquireAsync(fixture.ProjectId);
        var pendingDelete = service.DeleteAsync(
            fixture.ProjectId,
            created.Id,
            created.Revision);
        var chapter = new Chapter
        {
            ProjectId = fixture.ProjectId,
            Title = "Chapter",
        };
        var manuscript = ManuscriptCodec.FromPlainText(chapter.Id, "Opening", revision: 1);
        manuscript.Content[0] = manuscript.Content[0] with { StyleRole = "opening" };
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(manuscript);
        chapter.ManuscriptRevision = manuscript.Revision;
        fixture.Db.Chapters.Add(chapter);
        await fixture.Db.SaveChangesAsync();
        await lease.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pendingDelete);
        Assert.Single(await service.ListAsync(fixture.ProjectId));
    }

    [Fact]
    public async Task DatabaseConcurrencyRaceUsesThePublicStyleConflictContract()
    {
        await using var fixture = await StyleFixture.CreateAsync();
        var firstService = new ManuscriptStyleService(fixture.Db, fixture.Mutations);
        var created = await firstService.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                null,
                "Body",
                ManuscriptStyleKind.Paragraph,
                "body",
                new ManuscriptStyleProperties()));
        await using var secondDb = fixture.CreateAdditionalDbContext();
        _ = await secondDb.ManuscriptStyleDefinitions.SingleAsync(style => style.Id == created.Id);
        var secondService = new ManuscriptStyleService(secondDb, fixture.Mutations);
        var firstUpdated = await firstService.UpsertAsync(
            fixture.ProjectId,
            new ManuscriptStyleInput(
                created.Id,
                "Body A",
                created.Kind,
                created.SemanticRole,
                created.Definition,
                created.Revision));

        var exception = await Assert.ThrowsAsync<ManuscriptStyleConflictException>(() =>
            secondService.UpsertAsync(
                fixture.ProjectId,
                new ManuscriptStyleInput(
                    created.Id,
                    "Body B",
                    created.Kind,
                    created.SemanticRole,
                    created.Definition,
                    created.Revision)));
        Assert.Equal(firstUpdated.Revision, exception.ActualRevision);
    }

    private sealed class StyleFixture(
        SqliteConnection connection,
        AppDbContext db,
        Guid projectId) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public Guid ProjectId { get; } = projectId;
        public IProjectMutationCoordinator Mutations { get; } = new ProjectMutationCoordinator();

        public AppDbContext CreateAdditionalDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            return new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        }

        public static async Task<StyleFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await db.Database.MigrateAsync();
            var project = new Project
            {
                Name = "Styles",
                Slug = $"styles-{Guid.NewGuid():N}",
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            return new StyleFixture(connection, db, project.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

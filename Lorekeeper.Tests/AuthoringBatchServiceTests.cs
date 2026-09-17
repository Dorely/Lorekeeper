using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class AuthoringBatchServiceTests
{
    [Fact]
    public async Task ApplyBatchReplaysSameHashAndRejectsReusedIdentityWithDifferentContent()
    {
        await using var fixture = await Fixture.CreateAsync(chapterCount: 1);
        var chapterId = fixture.ChapterIds.Single();
        var batch = await fixture.CreateBatchAsync(
            [(chapterId, "Changed")]);

        var committed = await fixture.Service.ApplyBatchAsync(batch);
        var replayed = await fixture.Service.ApplyBatchAsync(batch);
        Assert.Equal(AuthoringBatchStatusV1.Committed, committed.Status);
        Assert.Equal(AuthoringBatchStatusV1.Replayed, replayed.Status);
        Assert.Equal(committed.ReceiptId, replayed.ReceiptId);
        Assert.Equal(committed.Sequence, replayed.Sequence);

        var conflicting = batch with
        {
            Operations = [batch.Operations.Single() with { Text = "Different" }],
        };
        conflicting = conflicting with { RequestHash = AuthoringBatchHash.Compute(conflicting) };
        await Assert.ThrowsAsync<AuthoringIdempotencyException>(() =>
            fixture.Service.ApplyBatchAsync(conflicting));

        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.Chapters.SingleAsync();
        Assert.Equal("Changed", persisted.Manuscript.Content[0].Content[0].Text);
        Assert.Equal(1, persisted.ManuscriptRevision);
        Assert.Single(await verify.AuthoringBatchReceipts.ToListAsync());
        Assert.Equal(1, (await verify.AuthoringSessions.SingleAsync()).LastSequence);
        Assert.Equal(0, fixture.Manuscripts.PreCommitDerivedRefreshCount);
        Assert.Equal(2, fixture.Manuscripts.PostCommitDerivedRefreshCount);
    }

    [Fact]
    public async Task MultiTargetBatchRollsBackMutationSequenceAndReceiptWhenAnyTargetFails()
    {
        await using var fixture = await Fixture.CreateAsync(chapterCount: 2);
        fixture.Manuscripts.FailChapterId = fixture.ChapterIds[1];
        var batch = await fixture.CreateBatchAsync(
            [(fixture.ChapterIds[0], "First changed"), (fixture.ChapterIds[1], "Second changed")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyBatchAsync(batch));

        await using var verify = fixture.CreateDbContext();
        var chapters = await verify.Chapters.OrderBy(item => item.Order).ToListAsync();
        Assert.Equal("Original 0", chapters[0].Manuscript.Content[0].Content[0].Text);
        Assert.Equal("Original 1", chapters[1].Manuscript.Content[0].Content[0].Text);
        Assert.Equal(0, (await verify.AuthoringSessions.SingleAsync()).LastSequence);
        Assert.Empty(await verify.AuthoringBatchReceipts.ToListAsync());
        Assert.Equal(0, fixture.Manuscripts.PreCommitDerivedRefreshCount);
        Assert.Equal(0, fixture.Manuscripts.PostCommitDerivedRefreshCount);
    }

    [Fact]
    public async Task CompoundUndoRestoreRollbackDoesNotRefreshDerivedStateBeforeCommit()
    {
        await using var fixture = await Fixture.CreateAsync(chapterCount: 2);
        var batch = await fixture.CreateBatchAsync(
            [(fixture.ChapterIds[0], "First edited"), (fixture.ChapterIds[1], "Second edited")]);
        _ = await fixture.Service.ApplyBatchAsync(batch);
        Assert.Equal(2, fixture.Manuscripts.PostCommitDerivedRefreshCount);

        fixture.Manuscripts.FailChapterId = fixture.ChapterIds[1];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UndoAsync(new(
            fixture.ProjectId,
            fixture.SessionId,
            Guid.NewGuid(),
            $"chapter:{fixture.ChapterIds[0]:D}",
            ExpectedGeneration: 0)));

        await using var verify = fixture.CreateDbContext();
        var chapters = await verify.Chapters.OrderBy(item => item.Order).ToListAsync();
        Assert.Equal("First edited", chapters[0].Manuscript.Content[0].Content[0].Text);
        Assert.Equal("Second edited", chapters[1].Manuscript.Content[0].Content[0].Text);
        Assert.All(chapters, item => Assert.Equal(1, item.ManuscriptRevision));
        Assert.Equal(1, (await verify.AuthoringSessions.SingleAsync()).LastSequence);
        Assert.Single(await verify.AuthoringBatchReceipts.ToListAsync());
        Assert.Equal(0, fixture.Manuscripts.PreCommitDerivedRefreshCount);
        Assert.Equal(2, fixture.Manuscripts.PostCommitDerivedRefreshCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly DbContextOptions<AppDbContext> _options;

        private Fixture(
            string directory,
            DbContextOptions<AppDbContext> options,
            Guid projectId,
            Guid sessionId,
            IReadOnlyList<Guid> chapterIds,
            TestManuscriptService manuscripts,
            AuthoringBatchService service)
        {
            _directory = directory;
            _options = options;
            ProjectId = projectId;
            SessionId = sessionId;
            ChapterIds = chapterIds;
            Manuscripts = manuscripts;
            Service = service;
        }

        public Guid ProjectId { get; }
        public Guid SessionId { get; }
        public IReadOnlyList<Guid> ChapterIds { get; }
        public TestManuscriptService Manuscripts { get; }
        public AuthoringBatchService Service { get; }

        public static async Task<Fixture> CreateAsync(int chapterCount)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "authoring-batch.db");
            var connectionString = $"Data Source={databasePath}";
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
            var projectId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var chapterIds = Enumerable.Range(0, chapterCount).Select(_ => Guid.NewGuid()).ToList();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                db.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Authoring batch fixture",
                    Slug = $"batch-{projectId:N}",
                });
                db.AuthoringSessions.Add(new AuthoringSession
                {
                    Id = sessionId,
                    ProjectId = projectId,
                    ProcessIncarnationId = Guid.NewGuid(),
                });
                foreach (var (chapterId, index) in chapterIds.Select((id, index) => (id, index)))
                {
                    var document = Document(chapterId, $"Original {index}");
                    db.Chapters.Add(new Chapter
                    {
                        Id = chapterId,
                        ProjectId = projectId,
                        Title = $"Chapter {index}",
                        Order = index,
                        ManuscriptJson = ManuscriptCodec.Serialize(document),
                        ManuscriptRevision = document.Revision,
                    });
                }
                await db.SaveChangesAsync();
            }

            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator(connectionString));
            var mutationContext = new AuthoringMutationContextAccessor();
            var manuscripts = new TestManuscriptService(database, mutationContext);
            var history = new AuthoringDeltaHistoryRuntime();
            var service = new AuthoringBatchService(
                database,
                manuscripts,
                null!,
                null!,
                mutationContext,
                history,
                new TestFence(),
                NullLogger<AuthoringBatchService>.Instance);
            return new(directory, options, projectId, sessionId, chapterIds, manuscripts, service);
        }

        public async Task<AuthoringBatchV1> CreateBatchAsync(
            IReadOnlyList<(Guid ChapterId, string Text)> changes)
        {
            await using var db = CreateDbContext();
            var targets = new List<AuthoringBatchTargetV1>();
            var operations = new List<AuthoringOperationV1>();
            for (var ordinal = 0; ordinal < changes.Count; ordinal++)
            {
                var targetId = $"chapter:{changes[ordinal].ChapterId:D}";
                var state = await AuthoringPersistence.ReadTargetAsync(
                    db, ProjectId, targetId, string.Empty, CancellationToken.None);
                var document = ManuscriptCodec.Deserialize(state.ManuscriptJson);
                targets.Add(new(ordinal, targetId, state.Revision, state.Generation, state.Fingerprint));
                operations.Add(new(
                    ordinal,
                    "replaceBlockText",
                    BlockId: document.Content[0].Id,
                    Text: changes[ordinal].Text,
                    ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(document.Content[0])));
            }
            var batch = new AuthoringBatchV1(
                ProjectId,
                SessionId,
                Guid.NewGuid(),
                1,
                string.Empty,
                "Edit chapters",
                targets,
                operations);
            return batch with { RequestHash = AuthoringBatchHash.Compute(batch) };
        }

        public AppDbContext CreateDbContext() =>
            new(_options, NullLogger<AppDbContext>.Instance);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static ManuscriptDocument Document(Guid manuscriptId, string text) => new()
        {
            ManuscriptId = manuscriptId,
            Revision = 0,
            Content =
            [
                new ManuscriptBlock
                {
                    Id = "block",
                    Type = ManuscriptBlockType.Paragraph,
                    StyleRole = ManuscriptStyleRoles.Body,
                    Content = [new ManuscriptInline { Text = text }],
                },
            ],
        };
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestManuscriptService(
        IAppDatabaseOperationFactory database,
        IAuthoringMutationContextAccessor mutationContext) : IManuscriptService
    {
        public Guid? FailChapterId { get; set; }
        public int PreCommitDerivedRefreshCount { get; private set; }
        public int PostCommitDerivedRefreshCount { get; private set; }

        public Task<ManuscriptSnapshot?> GetManuscriptAsync(EditorContentTarget target, Guid chapterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ManuscriptMutationResult> ReplaceDocumentAsync(
            EditorContentTarget target,
            Guid chapterId,
            long expectedRevision,
            ManuscriptDocument document,
            CancellationToken cancellationToken = default)
        {
            if (chapterId == FailChapterId)
                throw new InvalidOperationException("Injected second-target persistence failure.");
            if (!mutationContext.IsHistorySuppressed)
                PreCommitDerivedRefreshCount++;
            await using var operation = await database.OpenWriteAsync(cancellationToken);
            var chapter = await operation.Db.Chapters.SingleAsync(item => item.Id == chapterId, cancellationToken);
            if (chapter.ManuscriptRevision != expectedRevision)
                throw new DbUpdateConcurrencyException();
            var replacement = document with { ManuscriptId = chapterId, Revision = checked(expectedRevision + 1) };
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(replacement);
            chapter.ManuscriptRevision = replacement.Revision;
            await operation.SaveChangesAsync(cancellationToken);
            return new(
                new(chapterId, chapter.ManuscriptRevision, string.Empty, chapter.PlainText, replacement),
                replacement.Content.Select(item => item.Id).ToList());
        }

        public Task<ManuscriptMutationResult> ApplyAsync(EditorContentTarget target, Guid chapterId, long expectedRevision, IReadOnlyList<ManuscriptOperation> operations, CancellationToken cancellationToken = default) =>
            ApplyUnderProjectMutationLeaseAsync(target, chapterId, expectedRevision, operations, cancellationToken);

        public async Task<ManuscriptMutationResult> ApplyUnderProjectMutationLeaseAsync(
            EditorContentTarget target,
            Guid chapterId,
            long expectedRevision,
            IReadOnlyList<ManuscriptOperation> operations,
            CancellationToken cancellationToken = default)
        {
            if (chapterId == FailChapterId)
                throw new InvalidOperationException("Injected second-target persistence failure.");
            if (!mutationContext.IsHistorySuppressed)
                PreCommitDerivedRefreshCount++;
            await using var operation = await database.OpenWriteAsync(cancellationToken);
            var chapter = await operation.Db.Chapters.SingleAsync(item => item.Id == chapterId, cancellationToken);
            if (chapter.ManuscriptRevision != expectedRevision)
                throw new DbUpdateConcurrencyException();
            var applied = ManuscriptOperations.Apply(chapter.Manuscript, operations);
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(applied.Document);
            chapter.ManuscriptRevision = applied.Document.Revision;
            await operation.SaveChangesAsync(cancellationToken);
            return new(
                new(chapterId, chapter.ManuscriptRevision, string.Empty, chapter.PlainText, applied.Document),
                applied.ChangedBlockIds);
        }

        public Task RefreshDerivedStateAsync(
            EditorContentTarget target,
            Guid chapterId,
            CancellationToken cancellationToken = default)
        {
            PostCommitDerivedRefreshCount++;
            return Task.CompletedTask;
        }

        public Task ValidateDocumentReferencesAsync(EditorContentTarget target, Guid chapterId, ManuscriptDocument document, IReadOnlyList<ManuscriptStyleView>? styleCatalog = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestFence : IAuthoringMutationFence
    {
        public Guid ProcessIncarnationId { get; } = Guid.NewGuid();
        public ValueTask<IAsyncDisposable> RegisterWriterAsync(AuthoringWriterRegistration registration, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void UpdateWriterState(AuthoringWriterState state) => throw new NotSupportedException();
        public Task<T> ExecuteAsync<T>(AuthoringFenceRequest request, Func<AuthoringFenceContext, CancellationToken, Task<T>> consume, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

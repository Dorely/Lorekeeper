using Lorekeeper.Authoring;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Lorekeeper.Sources;
using Lorekeeper.VersionHistory.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class AuthoringBatchServiceTests
{
    [Fact]
    public async Task Evidence_detachment_rolls_back_all_citations_and_preserves_bibliographic_metadata()
    {
        await using var fixture = await Fixture.CreateAsync(chapterCount: 2);
        var sourceId = Guid.NewGuid();
        var extractionId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        var updatedAt = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = fixture.CreateDbContext())
        {
            db.PublicationEditions.Add(new() { Id = releaseId, ProjectId = fixture.ProjectId, Name = "Inherited release" });
            db.IngestSources.Add(new() { Id = sourceId, ProjectId = fixture.ProjectId, Title = "Source", UserInstructions = "" });
            db.SourceExtractionVersions.Add(new() { Id = extractionId, SourceId = sourceId, Extractor = "fixture", ExtractorVersion = "1", ContentHash = "hash", NormalizedText = "Evidence" });
            db.SourceLocations.Add(new() { Id = locationId, ProjectId = fixture.ProjectId, SourceId = sourceId, ExtractionVersionId = extractionId, VerificationHash = "hash" });
            db.BibliographicRecords.Add(new() { Id = recordId, ProjectId = fixture.ProjectId, SourceId = sourceId, Title = "Retained title", Publisher = "Retained publisher", Notes = "Author notes", UpdatedAt = updatedAt });
            foreach (var chapter in await db.Chapters.AsTracking().ToListAsync())
            {
                var manuscript = chapter.Manuscript;
                chapter.ManuscriptJson = ManuscriptCodec.Serialize(manuscript with
                {
                    Content = [manuscript.Content[0] with { Content = [.. manuscript.Content[0].Content,
                        new() { Id = "citation", Type = ManuscriptInlineType.Citation, Citation = new() { Items =
                            [new() { BibliographicRecordId = recordId, SourceLocationId = locationId, LocatorLabel = "page", LocatorValue = "42" }] } }] }],
                });
            }
            await db.SaveChangesAsync();
        }
        fixture.Manuscripts.FailChapterId = fixture.ChapterIds[1];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Sources.DetachBibliographicRecordAsync(fixture.ProjectId, recordId, updatedAt));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(sourceId, (await db.BibliographicRecords.SingleAsync()).SourceId);
            Assert.All(await db.Chapters.ToListAsync(), chapter => Assert.Equal(locationId,
                ManuscriptTraversal.EnumerateCitations(chapter.Manuscript).Single().Cluster.Items.Single().SourceLocationId));
            Assert.Empty(await db.AuthoringTargetGenerations.ToListAsync());
        }
        fixture.Manuscripts.FailChapterId = null;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => fixture.Sources.DetachBibliographicRecordAsync(fixture.ProjectId, recordId, updatedAt.AddDays(-1)));
        await fixture.Sources.DetachBibliographicRecordAsync(fixture.ProjectId, recordId, updatedAt);
        await using var verify = fixture.CreateDbContext();
        var record = await verify.BibliographicRecords.SingleAsync();
        Assert.Null(record.SourceId);
        Assert.Equal("Retained title", record.Title);
        Assert.Equal("Retained publisher", record.Publisher);
        Assert.Equal("Author notes", record.Notes);
        Assert.All(await verify.Chapters.ToListAsync(), chapter =>
        {
            var item = ManuscriptTraversal.EnumerateCitations(chapter.Manuscript).Single().Cluster.Items.Single();
            Assert.Equal(recordId, item.BibliographicRecordId);
            Assert.Equal("42", item.LocatorValue);
            Assert.Null(item.SourceLocationId);
        });
        Assert.Equal(4, await verify.AuthoringTargetGenerations.CountAsync());
        Assert.Empty(await verify.PublicationEditionChapterOverrides.ToListAsync());
        Assert.All(await verify.AuthoringTargetGenerations.ToListAsync(), generation => Assert.Equal(1, generation.Generation));
    }

    [Fact]
    public async Task Nested_note_edit_replays_receipt_and_round_trips_durable_undo_redo()
    {
        await using var fixture = await Fixture.CreateAsync(chapterCount: 1);
        var chapterId = fixture.ChapterIds.Single();
        ManuscriptDocument before;
        await using (var db = fixture.CreateDbContext())
        {
            var chapter = await db.Chapters.AsTracking().SingleAsync();
            before = chapter.Manuscript;
            before = before with
            {
                Content = [before.Content[0] with { Content =
                    [.. before.Content[0].Content, new() { Id = "reference", Type = ManuscriptInlineType.NoteReference, NoteId = "note" }] }],
                Notes = [new() { Id = "note", Content = [new() { Id = "note-text", Content = [new() { Text = "Before note" }] }] }],
            };
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(before);
            await db.SaveChangesAsync();
            var state = await AuthoringPersistence.ReadTargetAsync(db, fixture.ProjectId, $"chapter:{chapterId:D}", "", CancellationToken.None);
            Assert.Contains("note-text", state.ElementFingerprints!.Keys);
        }
        var batch = await fixture.CreateBatchAsync([(chapterId, "Unused")]);
        var note = ManuscriptTraversal.EnumerateText(before).Single(item => item.Block.Id == "note-text");
        batch = batch with { Operations = [new(0, "replaceInlineContent", BlockId: note.Block.Id,
            ExpectedElementFingerprint: AuthoringBatchReducer.Fingerprint(note.Block), Position: note.Start,
            InlineContent: [new() { Text = "After note" }])] };
        batch = batch with { RequestHash = AuthoringBatchHash.Compute(batch) };
        var committed = await fixture.Service.ApplyBatchAsync(batch);
        var replay = await fixture.Service.ApplyBatchAsync(batch);
        Assert.Equal(committed.ReceiptId, replay.ReceiptId);
        Assert.Equal("replaceInlineContent", Assert.Single(committed.Inverse.Operations).Kind);
        var undo = await fixture.Service.UndoAsync(new(fixture.ProjectId, fixture.SessionId, Guid.NewGuid(), $"chapter:{chapterId:D}", ExpectedGeneration: 0));
        Assert.Equal(AuthoringBatchStatusV1.Committed, undo.Batch!.Status);
        await using (var db = fixture.CreateDbContext())
            Assert.True(ManuscriptCodec.ContentEquals(before, (await db.Chapters.SingleAsync()).Manuscript));
        var redo = await fixture.Service.RedoAsync(new(fixture.ProjectId, fixture.SessionId, Guid.NewGuid(), $"chapter:{chapterId:D}", ExpectedGeneration: 0));
        Assert.Equal(AuthoringBatchStatusV1.Committed, redo.Batch!.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("After note", (await verify.Chapters.SingleAsync()).Manuscript.Notes[0].Content[0].Content[0].Text);
        Assert.Equal(3, await verify.AuthoringBatchReceipts.CountAsync());
    }

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
            AuthoringBatchService service,
            ProjectSourcesService sources)
        {
            _directory = directory;
            _options = options;
            ProjectId = projectId;
            SessionId = sessionId;
            ChapterIds = chapterIds;
            Manuscripts = manuscripts;
            Service = service;
            Sources = sources;
        }

        public Guid ProjectId { get; }
        public Guid SessionId { get; }
        public IReadOnlyList<Guid> ChapterIds { get; }
        public TestManuscriptService Manuscripts { get; }
        public AuthoringBatchService Service { get; }
        public ProjectSourcesService Sources { get; }

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

            var projectMutations = new ProjectMutationCoordinator(connectionString);
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                projectMutations);
            var mutationContext = new AuthoringMutationContextAccessor();
            var manuscripts = new TestManuscriptService(database, mutationContext);
            var history = new AuthoringDeltaHistoryRuntime();
            var service = new AuthoringBatchService(
                database,
                new AuthoringTargetMutationService(manuscripts, null!, null!),
                mutationContext,
                history,
                new TestFence(),
                NullLogger<AuthoringBatchService>.Instance);
            var sources = new ProjectSourcesService(database, null!, new AuthoringMutationFence(database, projectMutations, history),
                new AuthoringTargetMutationService(manuscripts, null!, null!), mutationContext,
                new AuthoringGenerationService(database, history), new ProjectVersionHistoryUiEvents());
            return new(directory, options, projectId, sessionId, chapterIds, manuscripts, service, sources);
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

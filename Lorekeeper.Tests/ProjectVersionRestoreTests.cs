using System.Reflection;
using System.Text;
using Lorekeeper.Context;
using Lorekeeper.Graph;
using Lorekeeper.ImportExport;
using Lorekeeper.Ingest;
using Lorekeeper.Knowledge;
using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Outline;
using Lorekeeper.Persistence;
using Lorekeeper.Search;
using Lorekeeper.VersionHistory.Restore;
using Lorekeeper.VersionHistory.Git;
using Lorekeeper.VersionHistory.Compare;
using Lorekeeper.VersionHistory.Services;
using Lorekeeper.VersionHistory.Snapshots;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ProjectVersionRestoreTests
{
    [Fact]
    public void SnapshotReaderRequiresCanonicalJsonAndExactSchemaFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid());
            var validRoot = Path.Combine(root, "valid");
            WriteSnapshotTree(validRoot, payload);
            var reader = new VersionHistorySnapshotReader();

            var artifact = reader.Read(validRoot, payload.RepositoryId, payload.ProjectId);
            Assert.Equal(payload.ProjectId, artifact.Payload.ProjectId);

            var unexpectedRoot = Path.Combine(root, "unexpected");
            WriteSnapshotTree(unexpectedRoot, payload);
            File.WriteAllText(Path.Combine(unexpectedRoot, "undeclared.txt"), "not part of schema v1");
            var unexpected = Assert.Throws<InvalidDataException>(
                () => reader.Read(unexpectedRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("file set is not exact", unexpected.Message, StringComparison.OrdinalIgnoreCase);

            var noncanonicalRoot = Path.Combine(root, "noncanonical");
            WriteSnapshotTree(
                noncanonicalRoot,
                payload,
                (path, bytes) => path == "project/project.json" ? [.. Encoding.UTF8.GetBytes(" "), .. bytes] : bytes);
            var noncanonical = Assert.Throws<InvalidDataException>(
                () => reader.Read(noncanonicalRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("not in canonical", noncanonical.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderPreservesChapterMetadataAndUsesDirectManuscriptJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chapterId = Guid.NewGuid();
            var chapter = CreateChapter(chapterId, "Complete chapter") with
            {
                ActId = Guid.NewGuid(),
                Body = "Legacy body is retained as chapter metadata.",
                ManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapterId, revision: 17)),
                ManuscriptRevision = 17,
                Order = 4,
                VisualMode = ChapterVisualMode.IllustratedProse,
                PageLayoutKind = ChapterPageLayoutKind.DoubleLandscape,
                PageLayoutJson = "{\"pageWidth\":11}",
                IllustrationLayoutJson = "{\"columns\":2}",
                ExplicitImageContextImageIds = [Guid.NewGuid(), Guid.NewGuid()],
            };
            var payload = CreatePayload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                chapter: chapter,
                acts: [new ProjectExportAct(chapter.ActId!.Value, "Act", "Act synopsis", 0)]);
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(snapshotRoot, payload);

            var manuscriptPath = Path.Combine(
                snapshotRoot,
                "narrative",
                "chapters",
                chapter.Id.ToString("N"),
                "manuscript.json");
            var manuscriptBytes = File.ReadAllBytes(manuscriptPath);
            Assert.StartsWith("{", Encoding.UTF8.GetString(manuscriptBytes));
            Assert.DoesNotContain("\\\"", Encoding.UTF8.GetString(manuscriptBytes));

            var actual = new VersionHistorySnapshotReader()
                .Read(snapshotRoot, payload.RepositoryId, payload.ProjectId)
                .Payload
                .Narrative
                .Chapters
                .Single();
            var expectedManuscript = Encoding.UTF8.GetString(
                VersionHistoryCanonicalJson.SerializeDirectManuscript(chapter.ManuscriptJson));
            var expectedMetadata = VersionHistoryCanonicalJson.Deserialize<VersionHistorySnapshotChapter>(
                VersionHistoryCanonicalJson.Serialize(
                    VersionHistorySnapshotChapter.FromProjectExportChapter(chapter)));

            Assert.Equal(expectedMetadata.Id, actual.Id);
            Assert.Equal(expectedMetadata.ActId, actual.ActId);
            Assert.Equal(expectedMetadata.Title, actual.Title);
            Assert.Equal(expectedManuscript, actual.ManuscriptJson);
            Assert.Equal(expectedMetadata.ManuscriptRevision, actual.ManuscriptRevision);
            Assert.Equal(expectedMetadata.Body, actual.Body);
            Assert.Equal(expectedMetadata.Synopsis, actual.Synopsis);
            Assert.Equal(expectedMetadata.Order, actual.Order);
            Assert.Equal(expectedMetadata.VisualMode, actual.VisualMode);
            Assert.Equal(expectedMetadata.PageLayoutKind, actual.PageLayoutKind);
            Assert.Equal(expectedMetadata.PageLayoutJson, actual.PageLayoutJson);
            Assert.Equal(expectedMetadata.IllustrationLayoutJson, actual.IllustrationLayoutJson);
            Assert.Equal(expectedMetadata.ExplicitImageContextImageIds, actual.ExplicitImageContextImageIds);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"accessToken\":\"secret\"}")]
    public void SnapshotReaderRejectsMalformedOrSensitiveDirectManuscriptJson(string manuscriptJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid(), chapter: CreateChapter(Guid.NewGuid(), "Chapter"));
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(
                snapshotRoot,
                payload,
                (path, bytes) => path.EndsWith("/manuscript.json", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes(manuscriptJson)
                    : bytes);

            var exception = Assert.Throws<InvalidDataException>(
                () => new VersionHistorySnapshotReader().Read(snapshotRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("json", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SnapshotReaderRejectsManuscriptOwnedByAnotherChapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chapter = CreateChapter(Guid.NewGuid(), "Chapter");
            var otherChapter = CreateChapter(Guid.NewGuid(), "Other chapter");
            var payload = CreatePayload(Guid.NewGuid(), Guid.NewGuid(), chapter: chapter);
            var snapshotRoot = Path.Combine(root, "snapshot");
            WriteSnapshotTree(
                snapshotRoot,
                payload,
                (path, bytes) => path.EndsWith("/manuscript.json", StringComparison.Ordinal)
                    ? VersionHistoryCanonicalJson.SerializeDirectManuscript(otherChapter.ManuscriptJson)
                    : bytes);

            var exception = Assert.Throws<InvalidDataException>(
                () => new VersionHistorySnapshotReader().Read(snapshotRoot, payload.RepositoryId, payload.ProjectId));
            Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MajorAreaMergeChangesOnlyTheSelectedArea()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var currentChapterId = Guid.NewGuid();
        var targetChapterId = Guid.NewGuid();
        var current = CreatePayload(
            repositoryId,
            projectId,
            projectName: "Current project",
            chapter: CreateChapter(currentChapterId, "Current chapter"));
        var target = CreatePayload(
            repositoryId,
            projectId,
            projectName: "Historical project",
            chapter: CreateChapter(targetChapterId, "Historical chapter"),
            graph: new VersionHistorySnapshotGraphArea(
                [new ProjectExportNode("Character", "historical", "Historical", [], DateTime.UnixEpoch, DateTime.UnixEpoch)],
                []));

        var merged = Merge(
            current,
            target,
            VersionHistoryRestoreSelection.ForMajorAreas(["project"]));

        Assert.Equal("Historical project", merged.Project.Project.Name);
        Assert.Equal(current.Narrative.Chapters.Select(item => item.Id), merged.Narrative.Chapters.Select(item => item.Id));
        Assert.Equal(current.Graph.Nodes.Select(item => item.Key), merged.Graph.Nodes.Select(item => item.Key));
        Assert.Equal(current.RepositoryId, merged.RepositoryId);
        Assert.Equal(current.ProjectId, merged.ProjectId);
    }

    [Fact]
    public void SelectedChapterMergeReplacesAddsRemovesAndRestoresOnlySelectedAnnotations()
    {
        var repositoryId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var currentChapterId = Guid.NewGuid();
        var removedChapterId = Guid.NewGuid();
        var unrelatedChapterId = Guid.NewGuid();
        var addedChapterId = Guid.NewGuid();
        var current = CreatePayload(
            repositoryId,
            projectId,
            chapters:
            [
                CreateChapter(currentChapterId, "Current title"),
                CreateChapter(removedChapterId, "Removed title"),
                CreateChapter(unrelatedChapterId, "Unrelated title"),
            ],
            annotations:
            [
                CreateAnnotation(Guid.NewGuid(), currentChapterId, "Current annotation"),
                CreateAnnotation(Guid.NewGuid(), removedChapterId, "Removed annotation"),
                CreateAnnotation(Guid.NewGuid(), unrelatedChapterId, "Unrelated annotation"),
            ]);
        var target = CreatePayload(
            repositoryId,
            projectId,
            chapters:
            [
                CreateChapter(currentChapterId, "Historical replacement"),
                CreateChapter(addedChapterId, "Added title"),
            ],
            annotations:
            [
                CreateAnnotation(Guid.NewGuid(), currentChapterId, "Historical annotation"),
                CreateAnnotation(Guid.NewGuid(), addedChapterId, "Added annotation"),
            ]);

        var merged = Merge(
            current,
            target,
            VersionHistoryRestoreSelection.ForSelectedChapters(
                [currentChapterId, removedChapterId, addedChapterId],
                VersionHistoryAnnotationRestoreMode.SelectedChapterAnnotations));

        Assert.Equal(
            new[] { currentChapterId, unrelatedChapterId, addedChapterId }.OrderBy(id => id),
            merged.Narrative.Chapters.Select(item => item.Id));
        Assert.Equal("Historical replacement", merged.Narrative.Chapters.Single(item => item.Id == currentChapterId).Title);
        Assert.Equal("Unrelated title", merged.Narrative.Chapters.Single(item => item.Id == unrelatedChapterId).Title);
        Assert.Equal(
            new[] { currentChapterId, unrelatedChapterId, addedChapterId }.OrderBy(id => id),
            merged.Narrative.Annotations.Select(item => item.ChapterId).OrderBy(id => id));
        Assert.Equal("Historical annotation", merged.Narrative.Annotations.Single(item => item.ChapterId == currentChapterId).NoteText);
        Assert.Equal("Unrelated annotation", merged.Narrative.Annotations.Single(item => item.ChapterId == unrelatedChapterId).NoteText);
    }

    [Fact]
    public async Task UnsafeCrossAreaDependencyFailsClosedBeforeMutatingLiveProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "restore-invalid.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await setup.Database.MigrateAsync();
                setup.Projects.Add(new Project
                {
                    Id = projectId,
                    Name = "Live project",
                    Slug = "live-project",
                });
                await setup.SaveChangesAsync();
            }

            var invalidPayload = CreatePayload(
                repositoryId,
                projectId,
                graph: new VersionHistorySnapshotGraphArea(
                    [],
                    [new ProjectExportEdge(
                        new ProjectExportNodeRef("Character", "missing-from-snapshot"),
                        new ProjectExportNodeRef("Character", "also-missing"),
                        "related",
                        [],
                        null,
                        DateTime.UnixEpoch,
                        DateTime.UnixEpoch)]));
            var manifest = new VersionHistorySnapshotManifest(
                VersionHistorySnapshotContract.FormatId,
                VersionHistorySnapshotContract.SchemaVersion,
                repositoryId,
                projectId,
                VersionHistorySnapshotContract.IncludedAreas,
                new string('c', 64),
                new string('d', 64),
                []);
            var loaded = new ProjectVersionLoadedCheckpoint(
                new GitCommitMetadata(
                    new string('a', 40),
                    new string('b', 40),
                    "invalid dependency",
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    []),
                manifest,
                invalidPayload,
                null);
            var service = new ProjectVersionRestoreService(
                new StubHistoryService(loaded),
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!);

            var exception = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.RestoreAsync(
                    projectId,
                    new string('a', 40),
                    VersionHistoryRestoreSelection.ForWholeProject()));

            Assert.Equal("MissingGraphDependency", exception.Code);
            await using var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            var project = await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId);
            Assert.Equal("Live project", project.Name);
            Assert.Equal("live-project", project.Slug);
            Assert.Equal(0, await verify.ProjectVersionCheckpoints.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckoutRequiresExactHeadAndRecordsOnlyTheImportedCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "checkout.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            var oldCommit = new string('a', 40);
            var targetCommit = new string('b', 40);
            var targetTree = new string('c', 40);
            var payload = CreatePayload(repositoryId, projectId, projectName: "Remote project");
            var artifact = new VersionHistorySnapshotArtifact(
                root,
                new VersionHistorySnapshotManifest(
                    VersionHistorySnapshotContract.FormatId,
                    VersionHistorySnapshotContract.SchemaVersion,
                    repositoryId,
                    projectId,
                    VersionHistorySnapshotContract.IncludedAreas,
                    new string('d', 64),
                    new string('e', 64),
                    []),
                payload);
            var loaded = new ProjectVersionLoadedCheckpoint(
                new GitCommitMetadata(
                    targetCommit,
                    targetTree,
                    "remote",
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    "test",
                    "test@example.invalid",
                    DateTimeOffset.UnixEpoch,
                    []),
                artifact.Manifest,
                payload,
                null);
            var history = new StubHistoryService(loaded)
            {
                Status = new ProjectVersionStatusView(
                    new ProjectVersionRepositoryView(
                        projectId,
                        repositoryId,
                        1,
                        null,
                        null,
                        oldCommit,
                        null,
                        null,
                        ProjectVersionRepositoryHealth.Healthy,
                        false,
                        null),
                    null),
            };

            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await setup.Database.MigrateAsync();
                var project = new Project
                {
                    Id = projectId,
                    Name = "Live project",
                    Slug = "live-project",
                };
                setup.Projects.Add(project);
                setup.ProjectVersionRepositories.Add(new ProjectVersionRepository
                {
                    Id = repositoryId,
                    ProjectId = projectId,
                    Project = project,
                    CreativeRevision = 1,
                    HeadCommitSha = oldCommit,
                });
                await setup.SaveChangesAsync();
            }

            var mutation = new ProjectMutationCoordinator($"Data Source={databasePath}");
            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                mutation);
            var service = new ProjectVersionRestoreService(
                history,
                database,
                mutation,
                new NoopOutlineGraphSync(),
                new NoopContextIndexingService(),
                new NoopIngestGraphSync(),
                new NoopGraphStore(),
                new NoopGraphAutoLinkService(),
                new NoopProjectSearchIndex());
            var checkout = new VersionHistoryValidatedProjectCheckout(
                projectId,
                artifact,
                targetCommit,
                targetTree);

            var mismatch = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.CheckoutValidatedSnapshotAsync(checkout));
            Assert.Equal("CheckoutHeadMismatch", mismatch.Code);
            Assert.Equal(0, history.CreateCheckpointCalls);

            history.Status = new ProjectVersionStatusView(
                new ProjectVersionRepositoryView(
                    projectId,
                    repositoryId,
                    1,
                    null,
                    null,
                    targetCommit,
                    artifact.Manifest.ContentHash,
                    null,
                    ProjectVersionRepositoryHealth.Healthy,
                    false,
                    null),
                null);
            await using (var advance = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await advance.ProjectVersionRepositories
                    .Where(item => item.Id == repositoryId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.HeadCommitSha, targetCommit)
                        .SetProperty(item => item.HeadContentHash, artifact.Manifest.ContentHash));
            }
            await using (var advanced = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var repository = await advanced.ProjectVersionRepositories.AsNoTracking()
                    .SingleAsync(item => item.Id == repositoryId);
                Assert.Equal(targetCommit, repository.HeadCommitSha);
                Assert.Equal(artifact.Manifest.ContentHash, repository.HeadContentHash);
            }
            var result = await service.CheckoutValidatedSnapshotAsync(checkout);

            Assert.True(result.CheckpointCreated);
            Assert.Equal(targetCommit, result.HeadCommitSha);
            Assert.Equal(0, history.CreateCheckpointCalls);
            await using var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            Assert.Equal("Remote project", (await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId)).Name);
            Assert.Equal(1, await verify.ProjectVersionCheckpoints.CountAsync(item => item.ProjectVersionRepositoryId == repositoryId));
            Assert.Equal(ProjectVersionCheckpointSource.Remote, await verify.ProjectVersionCheckpoints.Select(item => item.Source).SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPreservesStableCreativeIdsAndRefusesIdentityCollision()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "restore.db")}")
                .Options;
            await using (var setup = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
                await setup.Database.MigrateAsync();

            var projectId = Guid.NewGuid();
            var repositoryId = Guid.NewGuid();
            var actId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var chapterManuscript = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(chapterId));
            var artifact = new VersionHistorySnapshotArtifact(
                root,
                new VersionHistorySnapshotManifest(
                    VersionHistorySnapshotContract.FormatId,
                    VersionHistorySnapshotContract.SchemaVersion,
                    repositoryId,
                    projectId,
                    VersionHistorySnapshotContract.IncludedAreas,
                    new string('c', 64),
                    new string('d', 64),
                    []),
                new VersionHistorySnapshotPayload(
                    repositoryId,
                    projectId,
                    new VersionHistorySnapshotProjectArea(
                        new ProjectExportProject(projectId, "Imported project", "imported-project", "guidance", true, false)
                        {
                            // Schema-v1 snapshots may contain the former
                            // workflow field, but import must not restore it.
                            LegacyAiChangeApprovalEnabled = true,
                        },
                        null,
                        false,
                        []),
                    new VersionHistorySnapshotNarrativeArea(
                        null,
                        [],
                        [],
                        [new ProjectExportAct(actId, "Act one", "Synopsis", 0)],
                        [new ProjectExportChapter
                        {
                            Id = chapterId,
                            ActId = actId,
                            Title = "Chapter one",
                            ManuscriptJson = chapterManuscript,
                            ManuscriptRevision = 0,
                            Synopsis = "Chapter synopsis",
                            Order = 0,
                        }],
                        [],
                        [],
                        []),
                    new VersionHistorySnapshotGraphArea([], []),
                    new VersionHistorySnapshotSourcesArea([]),
                    new VersionHistorySnapshotAssetsArea([], [], []),
                    new VersionHistorySnapshotManuscriptArea([]),
                    new VersionHistorySnapshotCompositionArea([]),
                    new VersionHistorySnapshotPublicationArea(null, [], [])));

            var database = new AppDatabaseOperationFactory(
                new TestDbContextFactory(options),
                new AppDatabaseWriteCoordinator(),
                new ProjectMutationCoordinator($"Data Source={Path.Combine(root, "restore.db")}"));
            var service = new ProjectVersionRestoreService(
                null!,
                database,
                new ProjectMutationCoordinator($"Data Source={Path.Combine(root, "restore.db")}"),
                new NoopOutlineGraphSync(),
                new NoopContextIndexingService(),
                new NoopIngestGraphSync(),
                new NoopGraphStore(),
                new NoopGraphAutoLinkService(),
                new NoopProjectSearchIndex());
            var import = new VersionHistoryValidatedSnapshotImport(
                artifact,
                new string('a', 40),
                new string('b', 40));

            var result = await service.ImportValidatedSnapshotAsync(import);

            Assert.Equal(projectId, result.ProjectId);
            Assert.Equal(repositoryId, result.RepositoryId);
            Assert.Equal(ProjectVersionCheckpointKind.Imported, result.ImportedCheckpoint.Kind);
            await using (var verify = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                var project = await verify.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId);
                Assert.Equal("Imported project", project.Name);
                Assert.Equal("imported-project", project.Slug);
                Assert.False(project.ReviewEditsEnabled);
                Assert.Equal(actId, await verify.Acts.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync());
                Assert.Equal(chapterId, await verify.Chapters.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync());
                Assert.Equal(repositoryId, await verify.ProjectVersionRepositories.Where(item => item.ProjectId == projectId).Select(item => item.Id).SingleAsync());
                Assert.Equal(new string('a', 40), await verify.ProjectVersionCheckpoints.Where(item => item.ProjectVersionRepositoryId == repositoryId).Select(item => item.CommitSha).SingleAsync());
            }

            var collision = await Assert.ThrowsAsync<VersionHistoryRestoreException>(
                () => service.ImportValidatedSnapshotAsync(import));
            Assert.Equal("ProjectIdentityCollision", collision.Code);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static VersionHistorySnapshotPayload Merge(
        VersionHistorySnapshotPayload current,
        VersionHistorySnapshotPayload target,
        VersionHistoryRestoreSelection selection)
    {
        var method = typeof(ProjectVersionRestoreService).GetMethod(
            "MergePayload",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(ProjectVersionRestoreService).FullName, "MergePayload");
        return (VersionHistorySnapshotPayload)(method.Invoke(null, [current, target, selection])
            ?? throw new InvalidOperationException("The restore merge returned no payload."));
    }

    private static void WriteSnapshotTree(
        string root,
        VersionHistorySnapshotPayload payload,
        Func<string, byte[], byte[]>? transform = null)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["project/project.json"] = VersionHistoryCanonicalJson.Serialize(payload.Project),
            ["narrative/narrative.json"] = VersionHistoryCanonicalJson.Serialize(
                VersionHistorySnapshotNarrativeFile.FromArea(payload.Narrative)),
            ["graph/graph.json"] = VersionHistoryCanonicalJson.Serialize(payload.Graph),
            ["sources/sources.json"] = VersionHistoryCanonicalJson.Serialize(payload.Sources),
            ["assets/assets.json"] = VersionHistoryCanonicalJson.Serialize(payload.Assets),
            ["manuscript/styles.json"] = VersionHistoryCanonicalJson.Serialize(payload.Manuscript),
            ["composition/composition.json"] = VersionHistoryCanonicalJson.Serialize(payload.Composition),
            ["publication/publication.json"] = VersionHistoryCanonicalJson.Serialize(payload.Publication),
        };
        foreach (var chapter in payload.Narrative.Chapters)
        {
            var chapterDirectory = $"narrative/chapters/{chapter.Id:N}";
            files[$"{chapterDirectory}/chapter.json"] = VersionHistoryCanonicalJson.Serialize(
                VersionHistorySnapshotChapter.FromProjectExportChapter(chapter));
            files[$"{chapterDirectory}/manuscript.json"] = VersionHistoryCanonicalJson.SerializeDirectManuscript(
                chapter.ManuscriptJson);
        }
        if (transform is not null)
        {
            foreach (var path in files.Keys.ToList())
                files[path] = transform(path, files[path]);
        }

        var contentHash = VersionHistoryCanonicalJson.Sha256Hex(files.Select(item => (item.Key, item.Value)));
        var manifest = new VersionHistorySnapshotManifest(
            VersionHistorySnapshotContract.FormatId,
            VersionHistorySnapshotContract.SchemaVersion,
            payload.RepositoryId,
            payload.ProjectId,
            VersionHistorySnapshotContract.IncludedAreas,
            contentHash,
            string.Empty,
            files.Select(item => new VersionHistorySnapshotFile(
                item.Key,
                item.Value.LongLength,
                VersionHistoryCanonicalJson.Sha256Hex(item.Value))).ToList());
        manifest = manifest with
        {
            ManifestHash = VersionHistoryCanonicalJson.Sha256Hex(VersionHistoryCanonicalJson.Serialize(manifest)),
        };

        files[VersionHistorySnapshotContract.ManifestFileName] = VersionHistoryCanonicalJson.Serialize(manifest);
        foreach (var (path, bytes) in files)
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
        }
    }

    private static VersionHistorySnapshotPayload CreatePayload(
        Guid repositoryId,
        Guid projectId,
        string projectName = "Project",
        ProjectExportChapter? chapter = null,
        IReadOnlyList<ProjectExportChapter>? chapters = null,
        IReadOnlyList<ProjectExportManuscriptAnnotation>? annotations = null,
        IReadOnlyList<ProjectExportAct>? acts = null,
        VersionHistorySnapshotGraphArea? graph = null)
    {
        var selectedChapters = chapters ?? (chapter is null ? [] : [chapter]);
        return new VersionHistorySnapshotPayload(
            repositoryId,
            projectId,
            new VersionHistorySnapshotProjectArea(
                new ProjectExportProject(projectId, projectName, projectName.ToLowerInvariant().Replace(' ', '-'), "guidance", true, false),
                null,
                false,
                []),
            new VersionHistorySnapshotNarrativeArea(
                null,
                [],
                [],
                acts ?? [],
                selectedChapters,
                [],
                [],
                annotations ?? []),
            graph ?? new VersionHistorySnapshotGraphArea([], []),
            new VersionHistorySnapshotSourcesArea([]),
            new VersionHistorySnapshotAssetsArea([], [], []),
            new VersionHistorySnapshotManuscriptArea([]),
            new VersionHistorySnapshotCompositionArea([]),
            new VersionHistorySnapshotPublicationArea(null, [], []));
    }

    private static ProjectExportChapter CreateChapter(Guid id, string title) => new()
    {
        Id = id,
        Title = title,
        ManuscriptJson = ManuscriptCodec.Serialize(ManuscriptCodec.CreateEmpty(id)),
        ManuscriptRevision = 0,
        Synopsis = title + " synopsis",
        Order = 0,
    };

    private static ProjectExportManuscriptAnnotation CreateAnnotation(Guid id, Guid chapterId, string note) =>
        new(
            id,
            chapterId,
            "Chapter",
            null,
            null,
            ManuscriptAnnotationKind.Note,
            note,
            0,
            0,
            ManuscriptAnnotationAnchorState.Current,
            "block",
            0,
            "block",
            1,
            "quote",
            string.Empty,
            string.Empty,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch);

    private sealed class StubHistoryService(ProjectVersionLoadedCheckpoint checkpoint) : IProjectVersionHistoryService
    {
        public ProjectVersionStatusView? Status { get; set; }

        public int CreateCheckpointCalls { get; private set; }

        public Task<ProjectVersionRepositoryView?> GetRepositoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<ProjectVersionRepositoryView?>(null);

        public Task<ProjectVersionRepositoryView> EnsureRepositoryAsync(Guid projectId, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionRepositoryView>();

        public Task<ProjectVersionCheckpointView> CreateCheckpointAsync(Guid projectId, ProjectVersionCheckpointKind kind, string semanticMessage, string? requestKey = null, DateTimeOffset? authoredAt = null, CancellationToken cancellationToken = default)
        {
            CreateCheckpointCalls++;
            return Unsupported<ProjectVersionCheckpointView>();
        }

        public Task<ProjectVersionTimelineView?> GetTimelineAsync(Guid projectId, int maxCheckpoints = 100, int maxOperations = 100, CancellationToken cancellationToken = default) => Task.FromResult<ProjectVersionTimelineView?>(null);

        public Task<int> ClearFailedOperationNoticesAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<ProjectVersionStatusView?> GetStatusAsync(Guid projectId, bool includeCurrentSnapshotHash = true, CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task SetReviewEditsEnabledAsync(Guid projectId, bool enabled, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());

        public Task<ProjectVersionReviewView?> GetReviewAsync(Guid projectId, IReadOnlyCollection<ProjectVersionReviewTarget>? targets = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewView?>();

        public Task<ProjectVersionReviewChapter?> GetReviewChapterAsync(Guid projectId, Guid chapterId, EditorContentTarget contentTarget, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewChapter?>();

        public Task<ProjectVersionHistoricalChapterReview?> GetLatestAffectingChapterAsync(Guid projectId, Guid chapterId, EditorContentTarget contentTarget, int maxCommits = 100, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionHistoricalChapterReview?>();

        public Task<ProjectVersionHistoricalRestoreResult> RestoreHistoricalChapterAsync(Guid projectId, Guid chapterId, EditorContentTarget contentTarget, string historicalCommitSha, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Undo manuscript to historical checkpoint", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionHistoricalRestoreResult>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalCheckpointAsync(Guid projectId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved Review Edits", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalForOtherAsync(Guid projectId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved other project changes", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> CreateReviewApprovalForBlocksAsync(Guid projectId, ProjectVersionReviewTarget target, IReadOnlyCollection<string> blockIds, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved selected manuscript changes", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionCheckpointView> ApproveReviewCompositionAsync(Guid projectId, ProjectVersionReviewTarget target, Guid compositionId, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Approved Designed Page change", string? requestKey = null, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionCheckpointView>();

        public Task<ProjectVersionReviewBlockMutationResult> RestoreReviewBlocksAsync(Guid projectId, ProjectVersionReviewTarget target, IReadOnlyCollection<string> blockIds, ProjectVersionReviewConcurrencyToken expectedToken, string semanticMessage = "Undid selected manuscript changes", CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewBlockMutationResult>();

        public Task<ProjectVersionReviewBlockMutationResult> EditReviewBlockAsync(Guid projectId, ProjectVersionReviewTarget target, string blockId, string text, ProjectVersionReviewConcurrencyToken expectedToken, CancellationToken cancellationToken = default) => Unsupported<ProjectVersionReviewBlockMutationResult>();

        public Task<ProjectVersionLoadedCheckpoint> LoadCheckpointAsync(Guid projectId, string commitSha, CancellationToken cancellationToken = default) => Task.FromResult(checkpoint);

        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options, NullLogger<AppDbContext>.Instance);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopOutlineGraphSync : IOutlineGraphSync
    {
        public Task EnsureProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureActAsync(Act act, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureChapterAsync(Chapter chapter, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RepairProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopContextIndexingService : IContextIndexingService
    {
        public Task ReindexEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexChapterAsync(Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteChapterAsync(Guid projectId, Guid chapterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexActAsync(Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteActAsync(Guid projectId, Guid actId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexIngestSourceAsync(Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteIngestSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexIngestSourceChunkAsync(Guid sourceChunkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteIngestSourceChunkAsync(Guid projectId, Guid sourceChunkId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexProjectProfileAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteProjectProfileAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReindexWritingSampleAsync(Guid sampleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteWritingSampleAsync(Guid projectId, Guid sampleId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopGraphAutoLinkService : IGraphAutoLinkService
    {
        public Task<IReadOnlyList<GraphAutoMentionLink>> RefreshEntityAsync(Guid projectId, Guid entityId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphAutoMentionLink>>([]);
        public Task<IReadOnlyList<GraphAutoMentionLink>> RefreshSourceAsync(Guid projectId, string sourceType, Guid sourceId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphAutoMentionLink>>([]);
        public Task<IReadOnlyList<GraphAutoMentionLink>> ListEntityAutoMentionLinksAsync(Guid projectId, Guid entityId, int maxResults = 12, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphAutoMentionLink>>([]);
    }

    private sealed class NoopProjectSearchIndex : IProjectSearchIndex
    {
        public Task StoreAsync(ProjectSearchIndexChunk chunk, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreManyAsync(IEnumerable<ProjectSearchIndexChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProjectLexicalSearchResult>> SearchAsync(ProjectLexicalSearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectLexicalSearchResult>>([]);
        public Task DeleteBySourceAsync(string sourceType, string sourceId, string scopeKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByScopeAsync(string scopeKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopIngestGraphSync : IIngestGraphSync
    {
        public Task EnsureSourceAsync(IngestSource source, IReadOnlyList<IngestSourceChunk> sourceChunks, IReadOnlyList<IngestSourceBlock>? sourceBlocks = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveSourceAsync(Guid projectId, Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopGraphStore : IGraphStore
    {
        public Task<GraphNode> UpsertNodeAsync(Guid projectId, string nodeType, string key, string? label = null, IDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default) => Task.FromResult<GraphNode>(null!);
        public Task<GraphEdge> UpsertEdgeAsync(long fromNodeId, long toNodeId, string edgeType, IDictionary<string, object?>? properties = null, int? sortOrder = null, CancellationToken cancellationToken = default) => Task.FromResult<GraphEdge>(null!);
        public Task RemoveNodeAsync(long nodeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveEdgeAsync(long edgeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GraphNode?> GetNodeAsync(long nodeId, CancellationToken cancellationToken = default) => Task.FromResult<GraphNode?>(null);
        public Task<GraphNode?> FindNodeAsync(Guid projectId, string nodeType, string key, CancellationToken cancellationToken = default) => Task.FromResult<GraphNode?>(null);
        public Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(long nodeId, GraphTraversalOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphNode>>([]);
        public Task<IReadOnlyList<GraphPath>> FindPathsAsync(long fromNodeId, long toNodeId, GraphTraversalOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphPath>>([]);
    }
}

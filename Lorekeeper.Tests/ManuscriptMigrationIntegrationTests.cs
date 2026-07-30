using Lorekeeper.Manuscripts;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ManuscriptMigrationIntegrationTests
{
    private const string PreviousMigration = "20260716224731_AddChatMessageImageAttachments";

    [Fact]
    public async Task V7WalDatabaseMigratesWithBackupJournalAndExactText()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Alpha\r\n\r\nBeta Ω");

        await using (var db = fixture.CreateDbContext())
        {
            var service = fixture.CreateService();
            await service.ApplyPendingAsync(db);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var chapter = await db.Chapters.AsNoTracking().SingleAsync();
            Assert.Equal(chapterId, chapter.Id);
            Assert.Equal("Alpha\n\nBeta Ω", chapter.PlainText);
            Assert.Equal(1, chapter.ManuscriptRevision);
            var journal = await db.ManuscriptMigrationJournals.AsNoTracking().SingleAsync();
            Assert.Equal(ManuscriptMigrationStatus.Completed, journal.Status);
            Assert.Equal(journal.SourceHash, journal.TargetHash);
            Assert.True(File.Exists(journal.BackupPath));
        }
    }

    [Fact]
    public async Task RestoreRequiresTokenAndRestoresTheV7Backup()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Recover me");
        var service = fixture.CreateService();
        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);
        var state = await service.GetStateAsync();
        var backup = Assert.Single(
            state.Backups,
            item => item.Path.Contains("pre-manuscript", StringComparison.Ordinal));
        await using (var changedDb = fixture.CreateDbContext())
        {
            var chapter = await changedDb.Chapters.SingleAsync();
            var changed = ManuscriptCodec.ReparsePreservingBlockIds(chapter.Manuscript, "Changed after backup");
            chapter.ManuscriptJson = ManuscriptCodec.Serialize(changed);
            chapter.ManuscriptRevision = changed.Revision;
            await changedDb.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(backup.Path, "not-confirmed"));
        var request = await service.PrepareRestoreAsync(backup.Path);
        await service.RestoreAsync(request.BackupPath, request.ConfirmationToken);

        await using (var stillRunningDb = fixture.CreateDbContext())
            Assert.Equal("Changed after backup", (await stillRunningDb.Chapters.SingleAsync()).PlainText);
        await using (var restartedDb = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restartedDb);
        await using (var restoredDb = fixture.CreateDbContext())
            Assert.Equal("Recover me", (await restoredDb.Chapters.SingleAsync()).PlainText);
    }

    [Fact]
    public async Task LegacyRevisionSessionBecomesTerminalVersionedAuditState()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Original chapter");
        await fixture.AddLegacyRevisionSessionsAsync(chapterId);
        var service = fixture.CreateService();
        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);

        await using (var db = fixture.CreateDbContext())
        {
            var sessions = await db.EditorRevisionSessions.AsNoTracking()
                .OrderBy(session => session.Order)
                .ToListAsync();
            Assert.Equal(2, sessions.Count);
            Assert.Equal("Original chapter", sessions[0].OriginalPlainText);
            Assert.Equal(EditorRevisionSessionStatus.Failed, sessions[0].Status);
            Assert.Equal("legacy_line_edit_audit", sessions[0].OperationFormat);
            Assert.Contains("legacy-line-edit-v7", sessions[0].OperationsJson);
            Assert.Contains("legacy-revision-session-v7", sessions[0].ProposalJson);
            Assert.Contains("cannot be resumed", sessions[0].ErrorMessage);
            Assert.Equal("legacy_line_edit_audit", sessions[1].OperationFormat);
            Assert.Contains("legacy-line-edit-v7", sessions[1].OperationsJson);
            Assert.Contains("replacement text", sessions[1].OperationsJson);
            var job = await db.EditorRevisionJobs.AsNoTracking().SingleAsync();
            Assert.Equal(EditorRevisionJobStatus.Failed, job.Status);
            Assert.Contains("cannot be resumed", job.ErrorMessage);
        }

        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('EditorRevisionSessions');";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        Assert.DoesNotContain("StartLine", columns);
        Assert.DoesNotContain("EndLine", columns);
        Assert.DoesNotContain("ReplacementText", columns);
        Assert.DoesNotContain("MutationKind", columns);
    }

    [Fact]
    public async Task ActiveLegacyContestIsTerminalizedDuringMigration()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Original chapter");
        await fixture.AddActiveLegacyContestAsync(chapterId);
        var service = fixture.CreateService();
        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);

        await using var migrated = fixture.CreateDbContext();
        var batch = await migrated.ContestBatches.AsNoTracking().SingleAsync();
        var candidates = await migrated.ContestCandidates.AsNoTracking()
            .OrderBy(candidate => candidate.Order)
            .ToListAsync();
        Assert.Equal(ContestBatchStatus.Failed, batch.Status);
        Assert.Contains("cannot be resumed", batch.ErrorMessage);
        Assert.NotNull(batch.CompletedAt);
        Assert.Equal(ContestCandidateStatus.Failed, candidates[0].Status);
        Assert.Contains("cannot be resumed", candidates[0].ErrorMessage);
        Assert.Equal(ContestCandidateStatus.Completed, candidates[1].Status);
    }

    [Fact]
    public async Task ManuscriptRevisionRejectsConcurrentOverwrites()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Concurrent");
        var service = fixture.CreateService();
        await using (var migrationDb = fixture.CreateDbContext())
            await service.ApplyPendingAsync(migrationDb);

        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = await firstDb.Chapters.SingleAsync();
        var second = await secondDb.Chapters.SingleAsync();
        var firstDocument = ManuscriptCodec.ReparsePreservingBlockIds(first.Manuscript, "First writer");
        first.ManuscriptJson = ManuscriptCodec.Serialize(firstDocument);
        first.ManuscriptRevision = firstDocument.Revision;
        await firstDb.SaveChangesAsync();

        var secondDocument = ManuscriptCodec.ReparsePreservingBlockIds(second.Manuscript, "Second writer");
        second.ManuscriptJson = ManuscriptCodec.Serialize(secondDocument);
        second.ManuscriptRevision = secondDocument.Revision;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());
    }

    [Fact]
    public async Task RestartAfterSchemaApplicationResumesTheDataTransform()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Resume after schema");
        await using (var interruptedDb = fixture.CreateDbContext())
            await interruptedDb.Database.MigrateAsync();

        var service = fixture.CreateService();
        await using (var resumedDb = fixture.CreateDbContext())
            await service.ApplyPendingAsync(resumedDb);

        await using var verificationDb = fixture.CreateDbContext();
        var chapter = await verificationDb.Chapters.AsNoTracking().SingleAsync();
        Assert.Equal("Resume after schema", chapter.PlainText);
        Assert.Equal(
            ManuscriptMigrationStatus.Completed,
            (await verificationDb.ManuscriptMigrationJournals.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ConcurrentStartupAttemptsSerializeTheMigration()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("One migration");
        var firstService = fixture.CreateService();
        var secondService = fixture.CreateService();
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();

        await Task.WhenAll(
            firstService.ApplyPendingAsync(firstDb),
            secondService.ApplyPendingAsync(secondDb));

        await using var verificationDb = fixture.CreateDbContext();
        Assert.Equal("One migration", (await verificationDb.Chapters.AsNoTracking().SingleAsync()).PlainText);
        Assert.Single(await verificationDb.ManuscriptMigrationJournals.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ValidJsonLegacyProseIsNotMistakenForAManuscript()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("{}");
        await using (var interruptedDb = fixture.CreateDbContext())
            await interruptedDb.Database.MigrateAsync();

        var service = fixture.CreateService();
        await using (var resumedDb = fixture.CreateDbContext())
            await service.ApplyPendingAsync(resumedDb);

        await using var verificationDb = fixture.CreateDbContext();
        Assert.Equal("{}", (await verificationDb.Chapters.AsNoTracking().SingleAsync()).PlainText);
    }

    [Fact]
    public async Task PendingAiChangeRemainsApplicableAndTerminalChangeBecomesAuditData()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Before");
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);

        await using var verificationDb = fixture.CreateDbContext();
        var changes = await verificationDb.AiChanges.AsNoTracking()
            .OrderBy(change => change.Order)
            .ToListAsync();
        var pending = changes[0];
        Assert.Equal("ChapterManuscript", pending.ResourceKind);
        Assert.Equal("apply_manuscript_operations", pending.ToolName);
        var before = System.Text.Json.JsonSerializer.Deserialize<Lorekeeper.Outline.ChapterManuscriptChange>(
            pending.BeforeJson)!;
        var after = System.Text.Json.JsonSerializer.Deserialize<Lorekeeper.Outline.ChapterManuscriptChange>(
            pending.AfterJson)!;
        Assert.Equal(1, before.Revision);
        Assert.Equal(2, after.Revision);
        Assert.Equal("Before", before.PlainText);
        Assert.Equal("After", after.PlainText);
        Assert.Equal("LegacyChapterBodyAudit", changes[1].ResourceKind);
        Assert.Equal("legacy_chapter_body_audit", changes[1].ToolName);
        Assert.Contains("legacy-chapter-body-v7", changes[1].BeforeJson);
    }

    [Fact]
    public async Task FailedTransformStartsARecoveryShellAndPreservesTheOriginalBackup()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Cannot lose this");
        await fixture.AddInvalidIllustrationAsync();
        var service = fixture.CreateService();
        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);

        await using var recoveryDb = fixture.CreateDbContext();
        Assert.Empty(await recoveryDb.Projects.AsNoTracking().ToListAsync());
        var journal = await recoveryDb.ManuscriptMigrationJournals.AsNoTracking().SingleAsync();
        Assert.Equal(ManuscriptMigrationStatus.Failed, journal.Status);
        Assert.NotNull(journal.ErrorDetail);
        Assert.True(File.Exists(journal.BackupPath));
        Assert.True((await service.GetStateAsync()).RecoveryRequired);
    }

    private sealed class MigrationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "Lorekeeper.Tests",
            Guid.NewGuid().ToString("N"));

        public MigrationFixture()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = $"Data Source={Path.Combine(_directory, "fixture.db")}";
        }

        public string ConnectionString { get; }

        public AppDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(ConnectionString)
                .Options;
            return new AppDbContext(options, NullLogger<AppDbContext>.Instance);
        }

        public ManuscriptMigrationService CreateService()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                })
                .Build();
            return new ManuscriptMigrationService(
                configuration,
                NullLogger<ManuscriptMigrationService>.Instance);
        }

        public async Task<Guid> CreateV7DatabaseAsync(string body)
        {
            await using var db = CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            SqliteConnectionSettings.ConfigureDatabase(connection);
            var projectId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            await using (var project = connection.CreateCommand())
            {
                project.CommandText =
                    """
                    INSERT INTO Projects
                        (Id, AiChangeApprovalEnabled, ContestModeEnabled, CreatedAt,
                         IncludeCurrentChapterInContext, Name, ProjectGuidance, Slug, UpdatedAt)
                    VALUES
                        ($id, 1, 0, $now, 1, 'Fixture', '', $slug, $now);
                    """;
                project.Parameters.AddWithValue("$id", projectId.ToString().ToUpperInvariant());
                project.Parameters.AddWithValue("$slug", $"fixture-{projectId:N}");
                project.Parameters.AddWithValue("$now", DateTime.UtcNow);
                await project.ExecuteNonQueryAsync();
            }
            await using (var chapter = connection.CreateCommand())
            {
                chapter.CommandText =
                    """
                    INSERT INTO Chapters
                        (Id, ProjectId, ActId, Title, Body, Synopsis, VisualMode, PageLayoutKind,
                         PageLayoutJson, IllustrationLayoutJson, "Order", CreatedAt, UpdatedAt,
                         VectorIndexState, VectorIndexedAt, VectorIndexError)
                    VALUES
                        ($id, $projectId, NULL, 'Chapter', $body, '', 'Prose', 'SinglePortrait',
                         '', '', 0, $now, $now, 'Stale', NULL, NULL);
                    """;
                chapter.Parameters.AddWithValue("$id", chapterId.ToString().ToUpperInvariant());
                chapter.Parameters.AddWithValue("$projectId", projectId.ToString().ToUpperInvariant());
                chapter.Parameters.AddWithValue("$body", body);
                chapter.Parameters.AddWithValue("$now", DateTime.UtcNow);
                await chapter.ExecuteNonQueryAsync();
            }
            return chapterId;
        }

        public async Task AddLegacyRevisionSessionsAsync(Guid chapterId)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            Guid projectId;
            await using (var findProject = connection.CreateCommand())
            {
                findProject.CommandText = "SELECT ProjectId FROM Chapters WHERE Id = $chapterId;";
                findProject.Parameters.AddWithValue("$chapterId", chapterId.ToString().ToUpperInvariant());
                projectId = Guid.Parse((string)(await findProject.ExecuteScalarAsync())!);
            }

            var jobId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using (var job = connection.CreateCommand())
            {
                job.CommandText =
                    """
                    INSERT INTO EditorRevisionJobs
                        (Id, ProjectId, ConversationId, AssistantMessageId, ToolCallId,
                         ArgumentsJson, Status, ErrorMessage, CreatedAt, UpdatedAt, CompletedAt)
                    VALUES
                        ($id, $projectId, $conversationId, NULL, 'legacy-call', '{}',
                         'Running', NULL, $now, $now, NULL);
                    """;
                job.Parameters.AddWithValue("$id", jobId.ToString().ToUpperInvariant());
                job.Parameters.AddWithValue("$projectId", projectId.ToString().ToUpperInvariant());
                job.Parameters.AddWithValue("$conversationId", Guid.NewGuid().ToString().ToUpperInvariant());
                job.Parameters.AddWithValue("$now", now);
                await job.ExecuteNonQueryAsync();
            }

            foreach (var item in new[]
                     {
                         new { Order = 0, Status = "Queued", Completed = false },
                         new { Order = 1, Status = "Completed", Completed = true },
                     })
            {
                await using var session = connection.CreateCommand();
                session.CommandText =
                    """
                    INSERT INTO EditorRevisionSessions
                        (Id, JobId, "Order", ChapterId, ChapterTitle, Reason, Instructions,
                         OriginalChapterBody, ProviderId, ProviderName, ModelName, Status,
                         Summary, Rationale, MutationKind, StartLine, EndLine, ReplacementText,
                         Notes, ProposalJson, RawResponse, ErrorMessage, DurationMs, CreatedAt,
                         UpdatedAt, CompletedAt)
                    VALUES
                        ($id, $jobId, $order, $chapterId, 'Chapter', 'Reason', 'Instructions',
                         'Original chapter', NULL, 'Provider', 'Model', $status, 'Summary',
                         'Rationale', 'replace', 1, 1, 'replacement text', 'Notes', '{}',
                         'raw', NULL, 1.0, $now, $now, $completed);
                    """;
                session.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                session.Parameters.AddWithValue("$jobId", jobId.ToString().ToUpperInvariant());
                session.Parameters.AddWithValue("$order", item.Order);
                session.Parameters.AddWithValue("$chapterId", chapterId.ToString().ToUpperInvariant());
                session.Parameters.AddWithValue("$status", item.Status);
                session.Parameters.AddWithValue("$now", now);
                session.Parameters.AddWithValue("$completed", item.Completed ? now : DBNull.Value);
                await session.ExecuteNonQueryAsync();
            }
        }

        public async Task AddActiveLegacyContestAsync(Guid chapterId)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            Guid projectId;
            await using (var findProject = connection.CreateCommand())
            {
                findProject.CommandText = "SELECT ProjectId FROM Chapters WHERE Id = $chapterId;";
                findProject.Parameters.AddWithValue("$chapterId", chapterId.ToString().ToUpperInvariant());
                projectId = Guid.Parse((string)(await findProject.ExecuteScalarAsync())!);
            }

            var batchId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using (var batch = connection.CreateCommand())
            {
                batch.CommandText =
                    """
                    INSERT INTO ContestBatches
                        (Id, ProjectId, ConversationId, AssistantMessageId, ChapterId,
                         ChapterTitle, OriginalChapterBody, AcceptedChapterBody,
                         WinningCandidateId, ContextSnapshotJson, Status, ErrorMessage,
                         CreatedAt, UpdatedAt, CompletedAt)
                    VALUES
                        ($id, $projectId, $conversationId, NULL, $chapterId,
                         'Chapter', 'Original chapter', 'Original chapter',
                         NULL, '{}', 'Running', NULL, $now, $now, NULL);
                    """;
                batch.Parameters.AddWithValue("$id", batchId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$projectId", projectId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$conversationId", Guid.NewGuid().ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$chapterId", chapterId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$now", now);
                await batch.ExecuteNonQueryAsync();
            }

            foreach (var candidate in new[]
                     {
                         new { Order = 0, Status = "Pending" },
                         new { Order = 1, Status = "Completed" },
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO ContestCandidates
                        (Id, BatchId, "Order", ProviderId, ProviderName, ModelName,
                         Status, Summary, MutationsJson, ProposedBody, RawResponse,
                         ReviewStateJson, Notes, ErrorMessage, DurationMs, CreatedAt,
                         UpdatedAt, CompletedAt)
                    VALUES
                        ($id, $batchId, $order, 1, 'Provider', 'Model',
                         $status, 'Summary', '[]', 'Original chapter', '{}',
                         '{}', NULL, NULL, NULL, $now, $now, $completedAt);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$batchId", batchId.ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$order", candidate.Order);
                command.Parameters.AddWithValue("$status", candidate.Status);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue(
                    "$completedAt",
                    candidate.Status == "Completed" ? now : DBNull.Value);
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task AddLegacyAiChangesAsync(Guid chapterId)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            Guid projectId;
            await using (var findProject = connection.CreateCommand())
            {
                findProject.CommandText = "SELECT ProjectId FROM Chapters;";
                projectId = Guid.Parse((string)(await findProject.ExecuteScalarAsync())!);
            }

            var batchId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using (var batch = connection.CreateCommand())
            {
                batch.CommandText =
                    """
                    INSERT INTO AiChangeBatches
                        (Id, ProjectId, ConversationKind, ConversationId, AssistantMessageId,
                         Status, CreatedAt, UpdatedAt, ResolvedAt)
                    VALUES
                        ($id, $projectId, 'Editor', $conversationId, NULL, 'Pending',
                         $now, $now, NULL);
                    """;
                batch.Parameters.AddWithValue("$id", batchId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$projectId", projectId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$conversationId", Guid.NewGuid().ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$now", now);
                await batch.ExecuteNonQueryAsync();
            }

            var beforeJson = System.Text.Json.JsonSerializer.Serialize(
                new { Id = chapterId, Title = "Chapter", Body = "Before" });
            var afterJson = System.Text.Json.JsonSerializer.Serialize(
                new { Id = chapterId, Title = "Chapter", Body = "After" });
            foreach (var item in new[]
                     {
                         new { Order = 0, Status = "Pending" },
                         new { Order = 1, Status = "Applied" },
                     })
            {
                await using var change = connection.CreateCommand();
                change.CommandText =
                    """
                    INSERT INTO AiChanges
                        (Id, BatchId, "Order", ToolCallId, ToolName, ArgumentsJson, Summary,
                         BeforeJson, AfterJson, DraftAfterJson, ReviewStateJson, ResultJson,
                         ResourceKind, ResourceId, CreatedResourceIdsJson,
                         ReferencedResourceIdsJson, DependsOnChangeIdsJson, Status,
                         RejectionMessage, ErrorMessage, CreatedAt, UpdatedAt, ResolvedAt)
                    VALUES
                        ($id, $batchId, $order, 'tool-call', 'edit_chapter', '{}', 'Edit',
                         $before, $after, NULL, NULL, '{}', 'ChapterBody', $resourceId,
                         '[]', '[]', '[]', $status, NULL, NULL, $now, $now, NULL);
                    """;
                change.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                change.Parameters.AddWithValue("$batchId", batchId.ToString().ToUpperInvariant());
                change.Parameters.AddWithValue("$order", item.Order);
                change.Parameters.AddWithValue("$before", beforeJson);
                change.Parameters.AddWithValue("$after", afterJson);
                change.Parameters.AddWithValue("$resourceId", $"Chapter:{chapterId:N}");
                change.Parameters.AddWithValue("$status", item.Status);
                change.Parameters.AddWithValue("$now", now);
                await change.ExecuteNonQueryAsync();
            }
        }

        public async Task AddInvalidIllustrationAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                images = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        imageId = Guid.NewGuid(),
                        anchorPosition = "AfterParagraph",
                        paragraphIndex = 0,
                        paragraphHash = "0000000000000000",
                        widthPercent = 70,
                        alignment = "Center",
                        caption = string.Empty,
                        altTextOverride = string.Empty,
                        sortOrder = 0,
                        startOnNewPage = false,
                    },
                },
            });
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE Chapters SET VisualMode = 'IllustratedProse', IllustrationLayoutJson = $json;";
            command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}

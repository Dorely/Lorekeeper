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
    public async Task LaterPendingMigrationDoesNotDowngradeCurrentApplicationTables()
    {
        using var fixture = new MigrationFixture();
        var conversationId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260801003844_PublishConversationsV13");
            var projectId = Guid.NewGuid();
            await LegacyProjectSeed.InsertAsync(
                db,
                projectId,
                "Current book",
                $"current-{projectId:N}");
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO PublishConversations (Id, ProjectId, CreatedAt, UpdatedAt)
                VALUES ({conversationId}, {projectId}, {now}, {now});
                """);

            await fixture.CreateService().ApplyPendingAsync(db);
        }

        await using var verification = fixture.CreateDbContext();
        Assert.True(await verification.PublishConversations.AsNoTracking()
            .AnyAsync(conversation => conversation.Id == conversationId));
        Assert.Contains(
            "20260801022548_PublicationCoverImagesV14",
            await verification.Database.GetPendingMigrationsAsync());
    }

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
            var chapter = await changedDb.Chapters.AsTracking().SingleAsync();
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
        Assert.True(await fixture.CreateRecoveryService().ApplyScheduledRestoreAsync());
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
            var job = await db.EditorRevisionJobs.AsNoTracking()
                .Select(item => new { item.Status, item.ErrorMessage })
                .SingleAsync();
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
        var batch = await migrated.ContestBatches.AsNoTracking()
            .Select(item => new { item.Status, item.ErrorMessage, item.CompletedAt })
            .SingleAsync();
        var candidates = await migrated.ContestCandidates.AsNoTracking()
            .Select(candidate => new
            {
                candidate.Order,
                candidate.Status,
                candidate.ErrorMessage,
            })
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
    public async Task RestartAfterSchemaApplicationResumesTheDataTransform()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Resume after schema");
        await using (var interruptedDb = fixture.CreateDbContext())
            await interruptedDb.GetService<IMigrator>().MigrateAsync(
                ManuscriptMigrationService.SchemaV2EfMigrationId);

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
            await interruptedDb.GetService<IMigrator>().MigrateAsync(
                ManuscriptMigrationService.SchemaV2EfMigrationId);

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

        var changes = await fixture.ReadLegacyChangesAsync();
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
        using var arguments = System.Text.Json.JsonDocument.Parse(changes[1].ArgumentsJson);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Object,
            arguments.RootElement.GetProperty("payload").ValueKind);
        using var terminalBefore = System.Text.Json.JsonDocument.Parse(changes[1].BeforeJson);
        var beforePayload = terminalBefore.RootElement.GetProperty("payload");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, beforePayload.ValueKind);
        Assert.Equal(chapterId, beforePayload.GetProperty("Id").GetGuid());
        Assert.Equal("Before", beforePayload.GetProperty("Body").GetString());
        using var result = System.Text.Json.JsonDocument.Parse(changes[1].ResultJson);
        Assert.Equal(
            "OK. Replaced legacy lines.\nPreserved Ω and → exactly.",
            result.RootElement.GetProperty("payload").GetString());
    }

    [Fact]
    public async Task FailedTransformStartsARecoveryShellAndPreservesTheOriginalBackup()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Cannot lose this");
        _ = await fixture.AddLegacyIllustrationAsync("not-a-valid-hash");
        var service = fixture.CreateService();
        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);

        await using var recoveryDb = fixture.CreateDbContext();
        Assert.Empty(await recoveryDb.Projects.AsNoTracking().ToListAsync());
        var state = await service.GetStateAsync();
        Assert.True(state.RecoveryRequired);
        Assert.Contains(state.Backups, backup =>
            File.Exists(backup.Path)
            && Path.GetFileName(backup.Path).Contains("pre-manuscript", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleLegacyIllustrationHashPreservesTheRuntimeParagraphAnchor()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Current first paragraph\n\nSecond paragraph");
        var staleHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("Former first paragraph")))[..16].ToLowerInvariant();
        var (elementId, imageId) = await fixture.AddLegacyIllustrationAsync(staleHash);
        var service = fixture.CreateService();

        await using (var db = fixture.CreateDbContext())
            await service.ApplyPendingAsync(db);

        await using var verification = fixture.CreateDbContext();
        var chapter = await verification.Chapters.AsNoTracking().SingleAsync();
        var layoutJson = await fixture.ReadIllustrationLayoutJsonAsync();
        var layout = System.Text.Json.JsonSerializer.Deserialize<IllustratedProseLayout>(
            layoutJson,
            ManuscriptCodec.JsonOptions)!;
        var image = Assert.Single(layout.Images);
        Assert.Equal(elementId, image.Id);
        Assert.Equal(imageId, image.ImageId);
        Assert.Equal(chapter.Manuscript.Content[0].Id, image.BlockId);
        Assert.Equal(ChapterImageAnchorPosition.AfterParagraph, image.AnchorPosition);
        Assert.Equal(70, image.WidthPercent);
        Assert.Equal(ChapterImageAlignment.Center, image.Alignment);
        Assert.Equal("Legacy caption", image.Caption);
        Assert.Equal("Legacy alt text", image.AltTextOverride);
        Assert.Equal(3, image.SortOrder);
        Assert.True(image.StartOnNewPage);

        var journal = await verification.ManuscriptMigrationJournals.AsNoTracking().SingleAsync();
        using var report = System.Text.Json.JsonDocument.Parse(journal.ValidationReportJson);
        Assert.Equal(1, report.RootElement.GetProperty("StaleIllustrationAnchorHashCount").GetInt32());
    }

    [Fact]
    public async Task CurrentSchemaDatabaseUpgradesEveryV1PayloadAtomicallyAndIdempotently()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Versioned\n\ncontent");
        await fixture.AddLegacyRevisionSessionsAsync(chapterId);
        await fixture.AddActiveLegacyContestAsync(chapterId);
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var firstMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(firstMigration);
        await fixture.DowngradeAllStructuredJsonToV1Async();

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);
        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        await using var verification = fixture.CreateDbContext();
        var journals = await verification.ManuscriptMigrationJournals
            .AsNoTracking()
            .OrderBy(journal => journal.StartedAt)
            .ToListAsync();
        var v2Journal = Assert.Single(
            journals,
            journal => journal.MigrationName == ManuscriptMigrationService.SchemaV3MigrationName);
        Assert.Equal(ManuscriptMigrationStatus.Completed, v2Journal.Status);
        Assert.Equal(v2Journal.SourceHash, v2Journal.TargetHash);
        Assert.True(File.Exists(v2Journal.BackupPath));
        Assert.True(v2Journal.ChapterCount > 0);
        Assert.True(v2Journal.ContestBatchCount > 0);
        Assert.True(v2Journal.ContestCandidateCount > 0);
        Assert.True(v2Journal.RevisionSessionCount > 0);
        Assert.Equal(
            ManuscriptDocument.CurrentSchemaVersion,
            (await verification.Chapters.AsNoTracking().SingleAsync()).Manuscript.SchemaVersion);
        Assert.False(await fixture.AnySchemaV1PayloadsAsync());
    }

    [Fact]
    public async Task CurrentSchemaDatabaseUpgradesV5ManuscriptsToV6AtomicallyAndIdempotently()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Version five content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.DowngradeChapterToV5Async();

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);
        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        await using var verification = fixture.CreateDbContext();
        var journal = Assert.Single(
            await verification.ManuscriptMigrationJournals.AsNoTracking()
                .Where(item => item.MigrationName == ManuscriptMigrationService.SchemaV6MigrationName)
                .ToListAsync());
        Assert.Equal(5, journal.SourceSchemaVersion);
        Assert.Equal(ManuscriptDocument.CurrentSchemaVersion, journal.TargetSchemaVersion);
        Assert.Equal(journal.SourceHash, journal.TargetHash);
        var manuscript = (await verification.Chapters.AsNoTracking().SingleAsync()).Manuscript;
        Assert.Equal(ManuscriptDocument.CurrentSchemaVersion, manuscript.SchemaVersion);
        Assert.Empty(manuscript.Notes);
    }

    [Fact]
    public async Task HistoricalAuditPayloadsDoNotBlockSchemaV3Upgrade()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Versioned content");
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.SetHistoricalTerminalAuditPayloadsAsync();
        await fixture.DowngradeAllStructuredJsonToV1Async();

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);

        await using var verification = fixture.CreateDbContext();
        Assert.True(await verification.Projects.AnyAsync());
        Assert.Equal("null", await fixture.ReadLegacyAppliedFieldAsync("BeforeJson"));
        Assert.Equal("Linked two entities.", await fixture.ReadLegacyAppliedFieldAsync("ResultJson"));
        Assert.False((await service.GetStateAsync()).RecoveryRequired);
        Assert.False(await fixture.AnySchemaV1PayloadsAsync());
    }

    [Fact]
    public async Task MalformedStructuredAuditPayloadStillFailsClosed()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Versioned content");
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.SetMalformedStructuredAuditPayloadAsync();
        await fixture.DowngradeAllStructuredJsonToV1Async();

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);

        await using var verification = fixture.CreateDbContext();
        Assert.Empty(await verification.Projects.AsNoTracking().ToListAsync());
        var recovery = await service.GetStateAsync();
        Assert.True(recovery.RecoveryRequired);
        var error = Assert.IsType<string>((await fixture.CreateRecoveryService().GetStateAsync()).Error);
        Assert.Contains("malformed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StructurallyInvalidJsonResultPayloadStillFailsClosed()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Versioned content");
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.DowngradeAllStructuredJsonToV1Async();
        await fixture.SetStructurallyInvalidCurrentResultPayloadAsync();

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);

        await using var verification = fixture.CreateDbContext();
        Assert.Empty(await verification.Projects.AsNoTracking().ToListAsync());
        Assert.True((await service.GetStateAsync()).RecoveryRequired);
        var error = Assert.IsType<string>((await fixture.CreateRecoveryService().GetStateAsync()).Error);
        Assert.Contains("manuscript", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedCurrentChapterManuscriptIsNotReinterpretedAsLegacyProse()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.SetMalformedCurrentChapterManuscriptAsync();

        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        await using var verification = fixture.CreateDbContext();
        Assert.Empty(await verification.Projects.AsNoTracking().ToListAsync());
        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.True(recovery.RecoveryRequired);
        Assert.Contains(
            "malformed current manuscript",
            Assert.IsType<string>(recovery.Error),
            StringComparison.OrdinalIgnoreCase);
        var backupPath = Assert.IsType<string>(recovery.BackupPath);
        await using var protectedBackup = new SqliteConnection($"Data Source={backupPath}");
        await protectedBackup.OpenAsync();
        await using var select = protectedBackup.CreateCommand();
        select.CommandText = "SELECT ManuscriptJson FROM Chapters;";
        Assert.Equal("{malformed", await select.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaterEfBoundaryPreventsMalformedManuscriptResumeWithoutJournal()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await using (var advancedSchema = fixture.CreateDbContext())
        {
            await advancedSchema.GetService<IMigrator>().MigrateAsync(
                "20260730203619_PublicationEditionsV10");
            await advancedSchema.ManuscriptMigrationJournals.ExecuteDeleteAsync();
        }
        await fixture.SetMalformedCurrentChapterManuscriptAsync();

        var restartedService = fixture.CreateService();
        await using (var restart = fixture.CreateDbContext())
            await restartedService.ApplyPendingAsync(restart);

        await using var verification = fixture.CreateDbContext();
        Assert.Empty(await verification.Projects.AsNoTracking().ToListAsync());
        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.True(recovery.RecoveryRequired);
        Assert.Contains(
            "malformed current manuscript",
            Assert.IsType<string>(recovery.Error),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentSchemaDiscardedFailedAndInvalidCandidatesWithEmptyDraftsRemainHealthy()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.AdvanceToCurrentSchemaAsync();
        await fixture.AddCurrentContestAsync(
            chapterId,
            ("Failed", string.Empty, string.Empty),
            ("Invalid", string.Empty, string.Empty));

        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.False(recovery.RecoveryRequired, recovery.Error ?? "Recovery was requested without an error.");
    }

    [Fact]
    public async Task CurrentSchemaContestOriginalIsValidatedWithoutLegacyAcceptedColumn()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.AdvanceToCurrentSchemaAsync();
        var batch = await fixture.AddCurrentContestAsync(
            chapterId,
            ("Failed", string.Empty, string.Empty));
        await fixture.SetContestOriginalAsync(batch.BatchId, "{malformed");

        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.True(recovery.RecoveryRequired);
        Assert.Equal(ManuscriptMigrationService.SchemaV3MigrationName, recovery.MigrationName);
        Assert.Contains("malformed current manuscript", recovery.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentSchemaNonEmptyMalformedCandidateDraftFailsClosed()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.AdvanceToCurrentSchemaAsync();
        _ = await fixture.AddCurrentContestAsync(
            chapterId,
            ("Failed", string.Empty, "{malformed"));

        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.True(recovery.RecoveryRequired);
        Assert.Equal(ManuscriptMigrationService.SchemaV3MigrationName, recovery.MigrationName);
        Assert.Contains("malformed current manuscript", recovery.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentSchemaCompletedAndSelectedCandidatesRequireProposals()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.AdvanceToCurrentSchemaAsync();
        await fixture.AddCurrentContestAsync(
            chapterId,
            ("Completed", string.Empty, string.Empty),
            ("Selected", string.Empty, string.Empty));

        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.True(recovery.RecoveryRequired);
        Assert.Equal(ManuscriptMigrationService.SchemaV3MigrationName, recovery.MigrationName);
    }

    [Fact]
    public async Task CurrentSchemaV1CandidateDraftIsUpgradedWhenDraftColumnExists()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Current content");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.AdvanceToCurrentSchemaAsync();
        var contest = await fixture.AddCurrentContestAsync(
            chapterId,
            ("Failed", string.Empty, null));
        await fixture.SetCandidateDraftToSchemaV1Async(contest.CandidateId);

        await using (var restart = fixture.CreateDbContext())
            await service.ApplyPendingAsync(restart);

        var recovery = await fixture.CreateRecoveryService().GetStateAsync();
        Assert.False(recovery.RecoveryRequired, recovery.Error ?? "Recovery was requested without an error.");
        await using var verification = fixture.CreateDbContext();
        var draft = await verification.ContestCandidates
            .AsNoTracking()
            .Where(candidate => candidate.Id == contest.CandidateId)
            .Select(candidate => candidate.DraftManuscriptJson)
            .SingleAsync();
        Assert.Equal(ManuscriptDocument.CurrentSchemaVersion, ManuscriptCodec.Deserialize(draft).SchemaVersion);
    }

    [Fact]
    public async Task ScheduledRecoveryRestoresAndUpgradesDatabaseWithHistoricalAuditPayloads()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Recover versioned content");
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.SetHistoricalTerminalAuditPayloadsAsync();
        await fixture.DowngradeAllStructuredJsonToV1Async();

        var recovery = fixture.CreateRecoveryService();
        var protectedBackup = await recovery.CreateBackupAsync("manuscripts", "json-null-regression");
        await using (var failedDatabase = fixture.CreateDbContext())
        {
            await recovery.EnterRecoveryModeAsync(
                failedDatabase,
                protectedBackup,
                ManuscriptMigrationService.SchemaV3MigrationName,
                1,
                ManuscriptDocument.CurrentSchemaVersion,
                new InvalidDataException("Simulated prior migration failure."));
        }
        Assert.True((await service.GetStateAsync()).RecoveryRequired);

        var restore = await service.PrepareRestoreAsync(protectedBackup);
        await service.RestoreAsync(restore.BackupPath, restore.ConfirmationToken);
        Assert.True(await recovery.ApplyScheduledRestoreAsync());
        var restartedService = fixture.CreateService();
        await using (var restartedDatabase = fixture.CreateDbContext())
            await restartedService.ApplyPendingAsync(restartedDatabase);

        await using var verification = fixture.CreateDbContext();
        Assert.Equal("Recover versioned content", (await verification.Chapters.SingleAsync()).PlainText);
        Assert.Equal("null", await fixture.ReadLegacyAppliedFieldAsync("BeforeJson"));
        Assert.Equal("Linked two entities.", await fixture.ReadLegacyAppliedFieldAsync("ResultJson"));
        Assert.False((await restartedService.GetStateAsync()).RecoveryRequired);
        Assert.False(await fixture.AnySchemaV1PayloadsAsync());
    }

    [Fact]
    public async Task V1CustomRoleIsNormalizedMaterializedAndImmediatelyEditable()
    {
        using var fixture = new MigrationFixture();
        _ = await fixture.CreateV7DatabaseAsync("Styled text");
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.SetCustomV1ParagraphRoleAsync("Fancy Role</style>");
        Assert.True(await fixture.AnySchemaV1PayloadsAsync());

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);

        await using var verification = fixture.CreateDbContext();
        var journals = await verification.ManuscriptMigrationJournals.AsNoTracking().ToListAsync();
        Assert.True(
            await verification.Projects.AnyAsync(),
            string.Join(" | ", journals.Select(journal => journal.ErrorDetail)));
        var chapter = await verification.Chapters.AsTracking().SingleAsync();
        Assert.Equal(ManuscriptDocument.CurrentSchemaVersion, chapter.Manuscript.SchemaVersion);
        var style = await verification.ManuscriptStyleDefinitions.AsNoTracking().SingleAsync();
        Assert.Equal(style.SemanticRole, chapter.Manuscript.Content[0].StyleRole);
        Assert.True(ManuscriptSemanticRoles.IsValid(style.SemanticRole));
        ManuscriptStyleService.ValidateDocumentReferences(
            chapter.Manuscript,
            [
                new ManuscriptStyleView(
                    style.Id,
                    style.Name,
                    style.Kind,
                    style.SemanticRole,
                    System.Text.Json.JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                        style.DefinitionJson,
                        ManuscriptCodec.JsonOptions)!,
                    style.Revision),
            ]);

        var (edited, _) = ManuscriptOperations.Apply(
            chapter.Manuscript,
            [new ReplaceManuscriptBlockText(chapter.Manuscript.Content[0].Id, "Edited safely")]);
        chapter.ManuscriptJson = ManuscriptCodec.Serialize(edited);
        chapter.ManuscriptRevision = edited.Revision;
        await verification.SaveChangesAsync();
        Assert.Equal("Edited safely", (await verification.Chapters.AsNoTracking().SingleAsync()).PlainText);
    }

    [Fact]
    public async Task PendingAiPayloadCustomRoleIsMaterializedAndRemainsValidAfterUpgrade()
    {
        using var fixture = new MigrationFixture();
        var chapterId = await fixture.CreateV7DatabaseAsync("Current body");
        await fixture.AddLegacyAiChangesAsync(chapterId);
        var service = fixture.CreateService();
        await using (var initialMigration = fixture.CreateDbContext())
            await service.ApplyPendingAsync(initialMigration);
        await fixture.SetPendingAiAfterCustomV1RoleAsync("AI Opening</style>");

        await using (var upgrade = fixture.CreateDbContext())
            await service.ApplyPendingAsync(upgrade);

        await using var verification = fixture.CreateDbContext();
        var style = await verification.ManuscriptStyleDefinitions.AsNoTracking().SingleAsync();
        var pending = await fixture.ReadLegacyPendingChangeAsync();
        var proposed = Assert.Single(
            ManuscriptSchemaUpgrade.ExtractCurrentDocuments(pending.AfterJson));
        var styles = new[]
        {
            new ManuscriptStyleView(
                style.Id,
                style.Name,
                style.Kind,
                style.SemanticRole,
                System.Text.Json.JsonSerializer.Deserialize<ManuscriptStyleProperties>(
                    style.DefinitionJson,
                    ManuscriptCodec.JsonOptions)!,
                style.Revision),
        };
        ManuscriptStyleService.ValidateDocumentReferences(proposed, styles);
        Assert.Equal(style.SemanticRole, proposed.Content[0].StyleRole);
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
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
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
                new DatabaseMigrationRecoveryService(
                    configuration,
                    NullLogger<DatabaseMigrationRecoveryService>.Instance),
                NullLogger<ManuscriptMigrationService>.Instance);
        }

        public DatabaseMigrationRecoveryService CreateRecoveryService()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                })
                .Build();
            return new DatabaseMigrationRecoveryService(
                configuration,
                NullLogger<DatabaseMigrationRecoveryService>.Instance);
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

        public async Task AdvanceToCurrentSchemaAsync()
        {
            await using var db = CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync();
        }

        public async Task<(Guid BatchId, Guid CandidateId)> AddCurrentContestAsync(
            Guid chapterId,
            params (string Status, string ProposedManuscriptJson, string? DraftManuscriptJson)[] candidates)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            Guid projectId;
            string chapterTitle;
            string originalManuscriptJson;
            long originalRevision;
            await using (var chapter = connection.CreateCommand())
            {
                chapter.CommandText = """
                    SELECT ProjectId, Title, ManuscriptJson, ManuscriptRevision
                    FROM Chapters
                    WHERE Id = $chapterId;
                    """;
                chapter.Parameters.AddWithValue("$chapterId", chapterId.ToString().ToUpperInvariant());
                await using var reader = await chapter.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                projectId = Guid.Parse(reader.GetString(0));
                chapterTitle = reader.GetString(1);
                originalManuscriptJson = reader.GetString(2);
                originalRevision = reader.GetInt64(3);
            }

            var batchId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var originalHash = ManuscriptCodec.HashPlainText(
                ManuscriptCodec.ProjectPlainText(ManuscriptCodec.Deserialize(originalManuscriptJson)));
            await using (var batch = connection.CreateCommand())
            {
                batch.CommandText = """
                    INSERT INTO ContestBatches
                        (Id, ProjectId, ConversationId, AssistantMessageId, ChapterId,
                         ContentTargetKind, ContentTargetEditionId, ChapterTitle,
                         OriginalManuscriptJson, OriginalManuscriptRevision,
                         OriginalManuscriptHash, WinningCandidateId, SelectedCandidateId,
                         ContextSnapshotJson, Status, ErrorMessage, CreatedAt, UpdatedAt,
                         CompletedAt)
                    VALUES
                        ($id, $projectId, $conversationId, NULL, $chapterId,
                         'Core', NULL, $chapterTitle, $original, $revision,
                         $hash, NULL, NULL, '{}', 'Discarded', NULL, $now, $now, $now);
                    """;
                batch.Parameters.AddWithValue("$id", batchId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$projectId", projectId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$conversationId", Guid.NewGuid().ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$chapterId", chapterId.ToString().ToUpperInvariant());
                batch.Parameters.AddWithValue("$chapterTitle", chapterTitle);
                batch.Parameters.AddWithValue("$original", originalManuscriptJson);
                batch.Parameters.AddWithValue("$revision", originalRevision);
                batch.Parameters.AddWithValue("$hash", originalHash);
                batch.Parameters.AddWithValue("$now", now);
                Assert.Equal(1, await batch.ExecuteNonQueryAsync());
            }

            var firstCandidateId = Guid.Empty;
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                var candidateId = Guid.NewGuid();
                await using var row = connection.CreateCommand();
                row.CommandText = """
                    INSERT INTO ContestCandidates
                        (Id, BatchId, "Order", ProviderId, ProviderName, ModelName,
                         Status, Summary, MutationsJson, ProposedManuscriptJson,
                         DraftManuscriptJson, RawResponse, ReviewStateJson, Notes,
                         ErrorMessage, DurationMs, CreatedAt, UpdatedAt, CompletedAt)
                    VALUES
                        ($id, $batchId, $order, 1, 'Provider', 'Model', $status,
                         'Summary', '[]', $proposed, $draft, '', '{}', NULL, NULL,
                         NULL, $now, $now, $now);
                    """;
                row.Parameters.AddWithValue("$id", candidateId.ToString().ToUpperInvariant());
                row.Parameters.AddWithValue("$batchId", batchId.ToString().ToUpperInvariant());
                row.Parameters.AddWithValue("$order", index);
                row.Parameters.AddWithValue("$status", candidate.Status);
                row.Parameters.AddWithValue("$proposed", candidate.ProposedManuscriptJson);
                row.Parameters.AddWithValue("$draft", candidate.DraftManuscriptJson ?? string.Empty);
                row.Parameters.AddWithValue("$now", now);
                Assert.Equal(1, await row.ExecuteNonQueryAsync());
                if (index == 0)
                    firstCandidateId = candidateId;
            }

            return (batchId, firstCandidateId);
        }

        public async Task SetContestOriginalAsync(Guid batchId, string value)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ContestBatches SET OriginalManuscriptJson = $value WHERE Id = $id;";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", batchId.ToString().ToUpperInvariant());
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task SetCandidateDraftToSchemaV1Async(Guid candidateId)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            string draft;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT ManuscriptJson
                    FROM Chapters
                    LIMIT 1;
                    """;
                draft = (string)(await select.ExecuteScalarAsync())!;
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(draft)!.AsObject();
            node["schemaVersion"] = 1;
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE ContestCandidates SET DraftManuscriptJson = $draft WHERE Id = $id;";
            update.Parameters.AddWithValue("$draft", node.ToJsonString(ManuscriptCodec.JsonOptions));
            update.Parameters.AddWithValue("$id", candidateId.ToString().ToUpperInvariant());
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
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
                         new { Order = 0, Status = "Pending", Result = "{}" },
                         new
                         {
                             Order = 1,
                             Status = "Applied",
                             Result = "OK. Replaced legacy lines.\nPreserved Ω and → exactly.",
                         },
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
                         $before, $after, NULL, NULL, $result, 'ChapterBody', $resourceId,
                         '[]', '[]', '[]', $status, NULL, NULL, $now, $now, NULL);
                    """;
                change.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                change.Parameters.AddWithValue("$batchId", batchId.ToString().ToUpperInvariant());
                change.Parameters.AddWithValue("$order", item.Order);
                change.Parameters.AddWithValue("$before", beforeJson);
                change.Parameters.AddWithValue("$after", afterJson);
                change.Parameters.AddWithValue("$resourceId", $"Chapter:{chapterId:N}");
                change.Parameters.AddWithValue("$status", item.Status);
                change.Parameters.AddWithValue("$result", item.Result);
                change.Parameters.AddWithValue("$now", now);
                await change.ExecuteNonQueryAsync();
            }
        }

        public async Task<IReadOnlyList<LegacyChangeRow>> ReadLegacyChangesAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Order", ResourceKind, ToolName, BeforeJson, AfterJson,
                       ArgumentsJson, ResultJson, Status
                FROM AiChanges
                ORDER BY "Order";
                """;
            var rows = new List<LegacyChangeRow>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(new(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            return rows;
        }

        public async Task<LegacyChangeRow> ReadLegacyPendingChangeAsync()
        {
            var changes = await ReadLegacyChangesAsync();
            return Assert.Single(changes, change => change.Status == "Pending");
        }

        public async Task<string> ReadLegacyAppliedFieldAsync(string fieldName)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"{fieldName}\" FROM AiChanges WHERE Status = 'Applied';";
            return Assert.IsType<string>(await command.ExecuteScalarAsync());
        }

        public sealed record LegacyChangeRow(
            int Order,
            string ResourceKind,
            string ToolName,
            string BeforeJson,
            string AfterJson,
            string ArgumentsJson,
            string ResultJson,
            string Status);

        public async Task SetHistoricalTerminalAuditPayloadsAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE AiChanges SET BeforeJson = 'null', ResultJson = 'Linked two entities.' "
                + "WHERE Status = 'Applied';";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task SetMalformedStructuredAuditPayloadAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE AiChanges SET BeforeJson = '{malformed' WHERE Status = 'Applied';";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task SetStructurallyInvalidCurrentResultPayloadAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE AiChanges SET ResultJson = $result WHERE Status = 'Applied';";
            command.Parameters.AddWithValue(
                "$result",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = ManuscriptDocument.CurrentSchemaVersion,
                    manuscriptId = Guid.NewGuid(),
                    revision = 1,
                    content = new[]
                    {
                        new
                        {
                            id = string.Empty,
                            type = "paragraph",
                            styleRole = ManuscriptStyleRoles.Body,
                            content = Array.Empty<object>(),
                        },
                    },
                }));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task SetMalformedCurrentChapterManuscriptAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Chapters SET ManuscriptJson = '{malformed';";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task<(Guid ElementId, Guid ImageId)> AddLegacyIllustrationAsync(string paragraphHash)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            var elementId = Guid.NewGuid();
            var imageId = Guid.NewGuid();
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                images = new[]
                {
                    new
                    {
                        id = elementId,
                        imageId,
                        anchorPosition = "AfterParagraph",
                        paragraphIndex = 0,
                        paragraphHash,
                        widthPercent = 70,
                        alignment = "Center",
                        caption = "Legacy caption",
                        altTextOverride = "Legacy alt text",
                        sortOrder = 3,
                        startOnNewPage = true,
                    },
                },
            });
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE Chapters SET VisualMode = 'IllustratedProse', IllustrationLayoutJson = $json;";
            command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync();
            return (elementId, imageId);
        }

        public async Task<string> ReadIllustrationLayoutJsonAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT IllustrationLayoutJson FROM Chapters;";
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task DowngradeAllStructuredJsonToV1Async()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            foreach (var (table, columns) in JsonPayloadColumns)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"UPDATE {table} SET "
                    + string.Join(
                        ", ",
                        columns.Select(column =>
                            $"{column} = replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace({column}, '\"schemaVersion\":7', '\"schemaVersion\":1'), '\"schemaVersion\":6', '\"schemaVersion\":1'), '\"schemaVersion\":5', '\"schemaVersion\":1'), '\"schemaVersion\":4', '\"schemaVersion\":1'), '\"schemaVersion\":3', '\"schemaVersion\":1'), '\"schemaVersion\":2', '\"schemaVersion\":1'), 'schemaVersion\\\":7', 'schemaVersion\\\":1'), 'schemaVersion\\\":6', 'schemaVersion\\\":1'), 'schemaVersion\\\":5', 'schemaVersion\\\":1'), 'schemaVersion\\\":4', 'schemaVersion\\\":1'), 'schemaVersion\\\":3', 'schemaVersion\\\":1'), 'schemaVersion\\\":2', 'schemaVersion\\\":1')"))
                    + ";";
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task DowngradeChapterToV5Async()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT ManuscriptJson FROM Chapters;";
            var json = (string)(await select.ExecuteScalarAsync())!;
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node["schemaVersion"] = 5;
            node.Remove("notes");
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Chapters SET ManuscriptJson = $json;";
            update.Parameters.AddWithValue("$json", node.ToJsonString(ManuscriptCodec.JsonOptions));
            await update.ExecuteNonQueryAsync();
        }

        public async Task SetCustomV1ParagraphRoleAsync(string role)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            string json;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT ManuscriptJson FROM Chapters;";
                json = (string)(await select.ExecuteScalarAsync())!;
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node["schemaVersion"] = 1;
            node["content"]!.AsArray()[0]!["styleRole"] = role;
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE Chapters SET ManuscriptJson = $json;";
            update.Parameters.AddWithValue(
                "$json",
                node.ToJsonString(ManuscriptCodec.JsonOptions));
            await update.ExecuteNonQueryAsync();
        }

        public async Task SetPendingAiAfterCustomV1RoleAsync(string role)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            string json;
            Guid changeId;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT Id, AfterJson FROM AiChanges WHERE Status = 'Pending' LIMIT 1;";
                await using var reader = await select.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                changeId = Guid.Parse(reader.GetString(0));
                json = reader.GetString(1);
            }
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            Assert.True(RewriteFirstManuscript(root, role));
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE AiChanges SET AfterJson = $json WHERE Id = $id;";
            update.Parameters.AddWithValue(
                "$json",
                root.ToJsonString(ManuscriptCodec.JsonOptions));
            update.Parameters.AddWithValue("$id", changeId.ToString().ToUpperInvariant());
            await update.ExecuteNonQueryAsync();
        }

        private static bool RewriteFirstManuscript(
            System.Text.Json.Nodes.JsonNode node,
            string role)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["schemaVersion"]?.GetValue<int>() == ManuscriptDocument.CurrentSchemaVersion
                    && obj["content"] is System.Text.Json.Nodes.JsonArray content)
                {
                    obj["schemaVersion"] = 1;
                    content[0]!["styleRole"] = role;
                    return true;
                }
                foreach (var property in obj.ToList())
                {
                    if (property.Value is System.Text.Json.Nodes.JsonValue value
                        && value.TryGetValue<string>(out var embedded)
                        && embedded.TrimStart().StartsWith('{'))
                    {
                        var parsed = System.Text.Json.Nodes.JsonNode.Parse(embedded);
                        if (parsed is not null && RewriteFirstManuscript(parsed, role))
                        {
                            obj[property.Key] = parsed.ToJsonString(ManuscriptCodec.JsonOptions);
                            return true;
                        }
                    }
                    else if (property.Value is not null
                        && RewriteFirstManuscript(property.Value, role))
                    {
                        return true;
                    }
                }
            }
            else if (node is System.Text.Json.Nodes.JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null && RewriteFirstManuscript(item, role))
                        return true;
                }
            }
            return false;
        }

        public async Task<bool> AnySchemaV1PayloadsAsync()
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            foreach (var (table, columns) in JsonPayloadColumns)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT {string.Join(" || ", columns.Select(column => $"COALESCE({column}, '')"))} FROM {table};";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var value = reader.GetString(0);
                    if (value.Contains("schemaVersion\\\":1", StringComparison.Ordinal)
                        || value.Contains("\"schemaVersion\":1", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static readonly IReadOnlyList<(string Table, string[] Columns)> JsonPayloadColumns =
        [
            ("Chapters", ["ManuscriptJson"]),
            ("ContestBatches", ["OriginalManuscriptJson", "AcceptedManuscriptJson"]),
            ("ContestCandidates", ["ProposedManuscriptJson", "ReviewStateJson"]),
            ("EditorRevisionSessions", ["OriginalManuscriptJson", "OperationsJson", "ProposalJson"]),
            (
                "AiChanges",
                [
                    "ArgumentsJson",
                    "BeforeJson",
                    "AfterJson",
                    "DraftAfterJson",
                    "ReviewStateJson",
                    "ResultJson",
                ]),
        ];

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}

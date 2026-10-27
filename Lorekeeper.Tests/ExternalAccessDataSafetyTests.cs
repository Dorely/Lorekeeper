using Lorekeeper.ExternalAccess;
using Lorekeeper.Models;
using Lorekeeper.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lorekeeper.Tests;

public sealed class ExternalAccessDataSafetyTests
{
    [Fact]
    public async Task MigrationPreservesExistingCreativeContentAndStartsWithAccessDisabled()
    {
        await WithDatabaseAsync(async options =>
        {
            var projectId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261026050250_ExplicitImageModelConfiguration");
                db.Projects.Add(new Project { Id = projectId, Name = "Preserved manuscript project", Slug = "preserved", ProjectGuidance = "Authored direction" });
                await db.SaveChangesAsync();
            }
            await using var migrated = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await migrated.Database.MigrateAsync();
            Assert.Equal("Authored direction", (await migrated.Projects.SingleAsync(value => value.Id == projectId)).ProjectGuidance);
            Assert.False(await migrated.ExternalAccessSettings.AnyAsync(value => value.Enabled));
            Assert.Empty(await migrated.McpClientGrants.ToArrayAsync());
            Assert.Empty(await migrated.ExternalTransferCaptures.ToArrayAsync());
            Assert.Empty(await migrated.Database.GetPendingMigrationsAsync());
        });
    }

    [Fact]
    public async Task RecoveryMigrationPreservesCommittedReceiptsAndRepairsTheirProjectScope()
    {
        await WithDatabaseAsync(async options =>
        {
            var projectId = Guid.NewGuid();
            var clientId = Guid.NewGuid();
            var mutationId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var outcome = System.Text.Json.JsonSerializer.Serialize(new { committed = true, result = new { id = projectId } });
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261026163607_CompleteExternalWorkspaceContracts");
                db.Projects.Add(new Project { Id = projectId, Name = "Receipt preservation", Slug = "receipt-preservation" });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO McpClientGrants (Id, DisplayName, Enabled, CredentialHash, ProtectedCredential, PermissionsJson, ProjectIdsJson, CreatedAt, UpdatedAt, LastUsedAt, ClientKind)
                    VALUES ({clientId}, 'Fixture', 1, 'fixture', 'fixture', '[]', NULL, {now}, {now}, NULL, '');
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ExternalMutationReceipts (Id, ClientGrantId, WorkId, ProjectId, MutationId, Operation, RequestHash, ResultJson, CreatedAt)
                    VALUES ({receiptId}, {clientId}, NULL, NULL, {mutationId}, 'create_project', 'original-request', {outcome}, {now});
                    """);
            }
            await using var migrated = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            await migrated.Database.MigrateAsync();
            var receipt = await migrated.ExternalMutationReceipts.SingleAsync(value => value.MutationId == mutationId);
            Assert.Equal(projectId, receipt.ProjectId);
            Assert.Equal(outcome, receipt.ResultJson);
            Assert.Equal("original-request", receipt.RequestHash);
            Assert.False(receipt.RepairPending);
            Assert.Equal("[]", receipt.RepairTargetsJson);
            Assert.Equal("Other MCP client", (await migrated.McpClientGrants.SingleAsync()).ClientKind);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentAndRetryReceiptShareTheOuterCommitAcrossOwningServiceSavepoints(bool commit)
    {
        await WithDatabaseAsync(async options =>
        {
            var projectId = Guid.NewGuid();
            var clientId = Guid.NewGuid();
            var mutationId = Guid.NewGuid();
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await db.Database.MigrateAsync();
                db.Projects.Add(new Project { Id = projectId, Name = "Atomic external edit", Slug = "atomic", ProjectGuidance = "Before" });
                db.McpClientGrants.Add(new McpClientGrant { Id = clientId, DisplayName = "Fixture", CredentialHash = "fixture", ProtectedCredential = "fixture", PermissionsJson = "[]" });
                await db.SaveChangesAsync();
            }
            await using (var db = new AppDbContext(options, NullLogger<AppDbContext>.Instance))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                await using (var owner = await db.BeginMutationTransactionAsync(CancellationToken.None))
                {
                    var project = await db.Projects.AsTracking().SingleAsync(value => value.Id == projectId);
                    project.ProjectGuidance = "After";
                    await db.SaveChangesAsync();
                    await owner.CommitAsync();
                }
                db.ExternalMutationReceipts.Add(new ExternalMutationReceipt { ClientGrantId = clientId, ProjectId = projectId,
                    MutationId = mutationId, Operation = "update_project_guidance", RequestHash = "exact-request", ResultJson = "{\"committed\":true}" });
                await db.SaveChangesAsync();
                if (commit) await transaction.CommitAsync();
                else await transaction.RollbackAsync();
            }
            await using var verified = new AppDbContext(options, NullLogger<AppDbContext>.Instance);
            Assert.Equal(commit ? "After" : "Before", (await verified.Projects.SingleAsync(value => value.Id == projectId)).ProjectGuidance);
            Assert.Equal(commit ? 1 : 0, await verified.ExternalMutationReceipts.CountAsync(value => value.MutationId == mutationId));
        });
    }

    private static async Task WithDatabaseAsync(Func<DbContextOptions<AppDbContext>, Task> exercise)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lorekeeper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await exercise(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory, "external.db")}").Options);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

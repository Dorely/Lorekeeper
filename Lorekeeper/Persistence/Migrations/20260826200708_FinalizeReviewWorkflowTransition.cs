using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// Removes the pre-Git review queues after the guarded startup transform has
/// copied their effective state into the live project.
/// </summary>
public partial class FinalizeReviewWorkflowTransition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The transform is intentionally performed by the startup migration
        // owner before this migration is applied.  Keep these statements
        // tolerant of a database that was created after one of the legacy
        // tables had already been removed by an interrupted recovery.
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AiChanges\";");
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AiChangeBatches\";");
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"AssistantReviewBaselines\";");

        // AcceptedManuscriptJson was the mutable, cross-candidate contest
        // result.  Candidate drafts are now isolated in ContestCandidates.
        migrationBuilder.Sql("ALTER TABLE \"ContestBatches\" DROP COLUMN \"AcceptedManuscriptJson\";");

        migrationBuilder.CreateIndex(
            name: "IX_ContestBatches_ProjectId_Unresolved",
            table: "ContestBatches",
            column: "ProjectId",
            unique: true,
            filter: "\"Status\" IN ('Running', 'Completed', 'Failed')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ContestBatches_ProjectId_Unresolved",
            table: "ContestBatches");

        // The removed queues and mixed contest result are not recreated on
        // rollback.  Their durable state was materialized or preserved as a
        // candidate draft before this migration was applied.
    }
}

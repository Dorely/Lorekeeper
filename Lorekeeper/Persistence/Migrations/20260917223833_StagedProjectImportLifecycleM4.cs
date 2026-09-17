using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StagedProjectImportLifecycleM4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InputKind",
                table: "ProjectImportJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "LegacyJson");

            migrationBuilder.AddColumn<string>(
                name: "StagedFileKey",
                table: "ProjectImportJobs",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "StagedLength",
                table: "ProjectImportJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "StagedSha256",
                table: "ProjectImportJobs",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // JSON payloads embedded in the old durable-job rows cannot be
            // converted into app-owned staged files inside a SQLite migration.
            // Never resume an ambiguous pre-cutover mutation; retained terminal
            // reports remain readable while non-terminal rows fail closed.
            migrationBuilder.Sql("""
                UPDATE ProjectImportJobs
                SET Status = 'Failed',
                    CurrentMessage = 'This import predates staged-file recovery and must be uploaded again.',
                    ErrorMessage = 'The import was not resumed because its legacy embedded payload was removed by the streamed archive cutover.',
                    CompletedAt = COALESCE(CompletedAt, CURRENT_TIMESTAMP),
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE Status IN ('Queued', 'Running');
                """);

            migrationBuilder.DropColumn(
                name: "ContentJson",
                table: "ProjectImportJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentJson",
                table: "ProjectImportJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.DropColumn(
                name: "InputKind",
                table: "ProjectImportJobs");

            migrationBuilder.DropColumn(
                name: "StagedFileKey",
                table: "ProjectImportJobs");

            migrationBuilder.DropColumn(
                name: "StagedLength",
                table: "ProjectImportJobs");

            migrationBuilder.DropColumn(
                name: "StagedSha256",
                table: "ProjectImportJobs");

        }
    }
}

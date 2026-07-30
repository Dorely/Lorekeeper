using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StructuredManuscriptsV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OriginalChapterBody",
                table: "EditorRevisionSessions",
                newName: "OriginalManuscriptJson");

            migrationBuilder.RenameColumn(
                name: "ProposedBody",
                table: "ContestCandidates",
                newName: "ProposedManuscriptJson");

            migrationBuilder.RenameColumn(
                name: "OriginalChapterBody",
                table: "ContestBatches",
                newName: "OriginalManuscriptJson");

            migrationBuilder.RenameColumn(
                name: "AcceptedChapterBody",
                table: "ContestBatches",
                newName: "AcceptedManuscriptJson");

            migrationBuilder.RenameColumn(
                name: "Body",
                table: "Chapters",
                newName: "ManuscriptJson");

            migrationBuilder.Sql(
                """
                UPDATE EditorRevisionSessions
                SET ReplacementText = json_object(
                    'format', 'legacy-line-edit-v7',
                    'mutationKind', MutationKind,
                    'startLine', StartLine,
                    'endLine', EndLine,
                    'replacementText', ReplacementText);
                """);

            migrationBuilder.RenameColumn(
                name: "MutationKind",
                table: "EditorRevisionSessions",
                newName: "OperationFormat");

            migrationBuilder.RenameColumn(
                name: "ReplacementText",
                table: "EditorRevisionSessions",
                newName: "OperationsJson");

            migrationBuilder.DropColumn(
                name: "StartLine",
                table: "EditorRevisionSessions");

            migrationBuilder.DropColumn(
                name: "EndLine",
                table: "EditorRevisionSessions");

            migrationBuilder.AddColumn<long>(
                name: "ManuscriptRevision",
                table: "Chapters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ManuscriptMigrationJournals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MigrationName = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    BackupPath = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContestBatchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContestCandidateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RevisionSessionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceHash = table.Column<string>(type: "TEXT", nullable: false),
                    TargetHash = table.Column<string>(type: "TEXT", nullable: false),
                    ValidationReportJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManuscriptMigrationJournals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManuscriptMigrationJournals_MigrationName_StartedAt",
                table: "ManuscriptMigrationJournals",
                columns: new[] { "MigrationName", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManuscriptMigrationJournals");

            migrationBuilder.DropColumn(
                name: "ManuscriptRevision",
                table: "Chapters");

            migrationBuilder.AddColumn<int>(
                name: "StartLine",
                table: "EditorRevisionSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EndLine",
                table: "EditorRevisionSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.RenameColumn(
                name: "OperationFormat",
                table: "EditorRevisionSessions",
                newName: "MutationKind");

            migrationBuilder.RenameColumn(
                name: "OperationsJson",
                table: "EditorRevisionSessions",
                newName: "ReplacementText");

            migrationBuilder.RenameColumn(
                name: "OriginalManuscriptJson",
                table: "EditorRevisionSessions",
                newName: "OriginalChapterBody");

            migrationBuilder.RenameColumn(
                name: "ProposedManuscriptJson",
                table: "ContestCandidates",
                newName: "ProposedBody");

            migrationBuilder.RenameColumn(
                name: "OriginalManuscriptJson",
                table: "ContestBatches",
                newName: "OriginalChapterBody");

            migrationBuilder.RenameColumn(
                name: "AcceptedManuscriptJson",
                table: "ContestBatches",
                newName: "AcceptedChapterBody");

            migrationBuilder.RenameColumn(
                name: "ManuscriptJson",
                table: "Chapters",
                newName: "Body");
        }
    }
}

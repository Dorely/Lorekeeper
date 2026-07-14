using System;
using Lorekeeper.Llm;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProfessionalEditorBookBrief : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SystemPrompt",
                table: "Projects",
                newName: "ProjectGuidance");

            var escapedLegacySeed = SeedSystemPrompt.Default.Replace("'", "''");
            migrationBuilder.Sql(
                $"UPDATE \"Projects\" SET \"ProjectGuidance\" = '' WHERE \"ProjectGuidance\" = '{escapedLegacySeed}';");

            migrationBuilder.AddColumn<string>(
                name: "ContextSnapshotJson",
                table: "EditorMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookBriefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookKind = table.Column<string>(type: "TEXT", nullable: false),
                    Premise = table.Column<string>(type: "TEXT", nullable: false),
                    Genre = table.Column<string>(type: "TEXT", nullable: false),
                    PrimaryThemes = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    CreativeConstraints = table.Column<string>(type: "TEXT", nullable: false),
                    TargetAudience = table.Column<string>(type: "TEXT", nullable: false),
                    MinimumReaderAge = table.Column<int>(type: "INTEGER", nullable: true),
                    MaximumReaderAge = table.Column<int>(type: "INTEGER", nullable: true),
                    ReadingLevelGuidance = table.Column<string>(type: "TEXT", nullable: false),
                    TargetWordCount = table.Column<int>(type: "INTEGER", nullable: true),
                    PointOfView = table.Column<string>(type: "TEXT", nullable: false),
                    Tense = table.Column<string>(type: "TEXT", nullable: false),
                    VoiceAndTone = table.Column<string>(type: "TEXT", nullable: false),
                    LanguageLocale = table.Column<string>(type: "TEXT", nullable: false),
                    HouseStyle = table.Column<string>(type: "TEXT", nullable: false),
                    ReadAloudPriority = table.Column<bool>(type: "INTEGER", nullable: true),
                    AccessibilityGoals = table.Column<string>(type: "TEXT", nullable: false),
                    VisualDirection = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookBriefs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookBriefs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookBriefs_ProjectId",
                table: "BookBriefs",
                column: "ProjectId",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "BookBriefs" (
                    "Id", "ProjectId", "BookKind", "Premise", "Genre", "PrimaryThemes",
                    "Purpose", "CreativeConstraints", "TargetAudience", "MinimumReaderAge",
                    "MaximumReaderAge", "ReadingLevelGuidance", "TargetWordCount",
                    "PointOfView", "Tense", "VoiceAndTone", "LanguageLocale", "HouseStyle",
                    "ReadAloudPriority", "AccessibilityGoals", "VisualDirection", "CreatedAt", "UpdatedAt")
                SELECT
                    "Id", "Id", 'Unspecified', '', '', '', '', '', '', NULL, NULL, '', NULL,
                    '', '', '', '', '', NULL, '', '', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM "Projects";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookBriefs");

            migrationBuilder.DropColumn(
                name: "ContextSnapshotJson",
                table: "EditorMessages");

            migrationBuilder.RenameColumn(
                name: "ProjectGuidance",
                table: "Projects",
                newName: "SystemPrompt");
        }
    }
}

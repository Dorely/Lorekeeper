using System;
using Lorekeeper.Llm;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemPromptAndAiConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeCurrentChapterInContext",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Backfill existing projects with the current seed prompt.
            var escaped = SeedSystemPrompt.Default.Replace("'", "''");
            migrationBuilder.Sql($"UPDATE \"Projects\" SET \"SystemPrompt\" = '{escaped}' WHERE \"SystemPrompt\" = '';");

            migrationBuilder.CreateTable(
                name: "AiConsoleEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    SystemPromptSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseText = table.Column<string>(type: "TEXT", nullable: true),
                    ToolCallsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiConsoleEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiConsoleEntries_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiConsoleEntries_ProjectId_StartedAt",
                table: "AiConsoleEntries",
                columns: new[] { "ProjectId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiConsoleEntries");

            migrationBuilder.DropColumn(
                name: "IncludeCurrentChapterInContext",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "Projects");
        }
    }
}

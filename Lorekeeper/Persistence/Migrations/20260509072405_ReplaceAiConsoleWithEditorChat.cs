using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAiConsoleWithEditorChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AiChangeBatches_OutlineConversations_ConversationId",
                table: "AiChangeBatches");

            migrationBuilder.DropTable(
                name: "AiConsoleEntries");

            migrationBuilder.DropIndex(
                name: "IX_AiChangeBatches_ConversationId",
                table: "AiChangeBatches");

            migrationBuilder.AddColumn<string>(
                name: "ConversationKind",
                table: "AiChangeBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "Outline");

            migrationBuilder.CreateTable(
                name: "EditorConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditorConversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EditorMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditorMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditorMessages_EditorConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "EditorConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EditorConversations_ProjectId",
                table: "EditorConversations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EditorMessages_ConversationId_Order",
                table: "EditorMessages",
                columns: new[] { "ConversationId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EditorMessages");

            migrationBuilder.DropTable(
                name: "EditorConversations");

            migrationBuilder.Sql("DELETE FROM \"AiChangeBatches\" WHERE \"ConversationKind\" = 'Editor';");

            migrationBuilder.DropColumn(
                name: "ConversationKind",
                table: "AiChangeBatches");

            migrationBuilder.CreateTable(
                name: "AiConsoleEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseText = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SystemPromptSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "TEXT", nullable: false)
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
                name: "IX_AiChangeBatches_ConversationId",
                table: "AiChangeBatches",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiConsoleEntries_ProjectId_StartedAt",
                table: "AiConsoleEntries",
                columns: new[] { "ProjectId", "StartedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AiChangeBatches_OutlineConversations_ConversationId",
                table: "AiChangeBatches",
                column: "ConversationId",
                principalTable: "OutlineConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

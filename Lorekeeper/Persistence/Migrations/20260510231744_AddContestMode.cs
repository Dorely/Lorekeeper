using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContestMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContestModeEnabled",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ContestProviderSlot1Id",
                table: "Projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContestProviderSlot2Id",
                table: "Projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContestProviderSlot3Id",
                table: "Projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContestBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssistantMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChapterTitle = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalChapterBody = table.Column<string>(type: "TEXT", nullable: false),
                    OperationKind = table.Column<string>(type: "TEXT", nullable: false),
                    UserGoal = table.Column<string>(type: "TEXT", nullable: false),
                    MutationInstructions = table.Column<string>(type: "TEXT", nullable: false),
                    TargetRangesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContextSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContestBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContestBatches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContestCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    MutationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProposedBody = table.Column<string>(type: "TEXT", nullable: false),
                    RawResponse = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContestCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContestCandidates_ContestBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ContestBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContestBatches_ConversationId_CreatedAt",
                table: "ContestBatches",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContestBatches_ProjectId_Status_CreatedAt",
                table: "ContestBatches",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContestCandidates_BatchId_Order",
                table: "ContestCandidates",
                columns: new[] { "BatchId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContestCandidates");

            migrationBuilder.DropTable(
                name: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "ContestModeEnabled",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ContestProviderSlot1Id",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ContestProviderSlot2Id",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ContestProviderSlot3Id",
                table: "Projects");
        }
    }
}

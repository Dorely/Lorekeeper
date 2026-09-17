using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthoringBatchJournalV37 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthoringSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProcessIncarnationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthoringSessions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthoringTargetGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringTargetGenerations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthoringTargetGenerations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthoringBatchReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthoringBatchReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthoringBatchReceipts_AuthoringSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AuthoringSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AuthoringBatchReceipts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringBatchReceipts_BatchId",
                table: "AuthoringBatchReceipts",
                column: "BatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringBatchReceipts_ProjectId_AcknowledgedAt_CreatedAt",
                table: "AuthoringBatchReceipts",
                columns: new[] { "ProjectId", "AcknowledgedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringBatchReceipts_SessionId_Sequence",
                table: "AuthoringBatchReceipts",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringSessions_ProjectId_UpdatedAt",
                table: "AuthoringSessions",
                columns: new[] { "ProjectId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthoringTargetGenerations_ProjectId_TargetId",
                table: "AuthoringTargetGenerations",
                columns: new[] { "ProjectId", "TargetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthoringBatchReceipts");

            migrationBuilder.DropTable(
                name: "AuthoringTargetGenerations");

            migrationBuilder.DropTable(
                name: "AuthoringSessions");
        }
    }
}

using System;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260526120000_RemoveIngestReportEvidence")]
    public partial class RemoveIngestReportEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "__temp__IngestReportItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceChunkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GraphNodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    GraphEdgeId = table.Column<long>(type: "INTEGER", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestReportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestReportItems_IngestJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IngestJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngestReportItems_IngestSourceChunks_SourceChunkId",
                        column: x => x.SourceChunkId,
                        principalTable: "IngestSourceChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.Sql("""
                INSERT INTO "__temp__IngestReportItems" (
                    "Id", "JobId", "SourceChunkId", "Kind", "Status", "Title", "Summary", "Notes",
                    "ResourceType", "EntityId", "GraphNodeId", "GraphEdgeId", "PayloadJson", "ErrorMessage",
                    "CreatedAt", "UpdatedAt", "DeletedAt")
                SELECT
                    "Id", "JobId", "SourceChunkId", "Kind", "Status", "Title", "Summary", "Notes",
                    "ResourceType", "EntityId", "GraphNodeId", "GraphEdgeId", "PayloadJson", "ErrorMessage",
                    "CreatedAt", "UpdatedAt", "DeletedAt"
                FROM "IngestReportItems";
                """);

            migrationBuilder.DropTable(name: "IngestReportItems");
            migrationBuilder.RenameTable(name: "__temp__IngestReportItems", newName: "IngestReportItems");
            RecreateIndexes(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "__temp__IngestReportItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceChunkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GraphNodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    GraphEdgeId = table.Column<long>(type: "INTEGER", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestReportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestReportItems_IngestJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IngestJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngestReportItems_IngestSourceChunks_SourceChunkId",
                        column: x => x.SourceChunkId,
                        principalTable: "IngestSourceChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.Sql("""
                INSERT INTO "__temp__IngestReportItems" (
                    "Id", "JobId", "SourceChunkId", "Kind", "Status", "Title", "Summary", "Notes", "Evidence",
                    "ResourceType", "EntityId", "GraphNodeId", "GraphEdgeId", "PayloadJson", "ErrorMessage",
                    "CreatedAt", "UpdatedAt", "DeletedAt")
                SELECT
                    "Id", "JobId", "SourceChunkId", "Kind", "Status", "Title", "Summary", "Notes", '',
                    "ResourceType", "EntityId", "GraphNodeId", "GraphEdgeId", "PayloadJson", "ErrorMessage",
                    "CreatedAt", "UpdatedAt", "DeletedAt"
                FROM "IngestReportItems";
                """);

            migrationBuilder.DropTable(name: "IngestReportItems");
            migrationBuilder.RenameTable(name: "__temp__IngestReportItems", newName: "IngestReportItems");
            RecreateIndexes(migrationBuilder);
        }

        private static void RecreateIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IngestReportItems_GraphEdgeId",
                table: "IngestReportItems",
                column: "GraphEdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestReportItems_GraphNodeId",
                table: "IngestReportItems",
                column: "GraphNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestReportItems_JobId_Kind_Status_CreatedAt",
                table: "IngestReportItems",
                columns: new[] { "JobId", "Kind", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestReportItems_SourceChunkId",
                table: "IngestReportItems",
                column: "SourceChunkId");
        }
    }
}

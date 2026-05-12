using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectImportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentJson = table.Column<string>(type: "TEXT", nullable: false),
                    FormatId = table.Column<string>(type: "TEXT", nullable: false),
                    FormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ExportKind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    TotalSteps = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedSteps = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedNodeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MergedNodeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedEdgeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MergedEdgeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedActCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedChapterCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedBeatCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImportJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImportJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectImportReportItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GraphNodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    GraphEdgeId = table.Column<long>(type: "INTEGER", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImportReportItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImportReportItems_ProjectImportJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ProjectImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImportJobs_ProjectId_Status_CreatedAt",
                table: "ProjectImportJobs",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImportReportItems_GraphEdgeId",
                table: "ProjectImportReportItems",
                column: "GraphEdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImportReportItems_GraphNodeId",
                table: "ProjectImportReportItems",
                column: "GraphNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImportReportItems_JobId_Kind_Status_CreatedAt",
                table: "ProjectImportReportItems",
                columns: new[] { "JobId", "Kind", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectImportReportItems");

            migrationBuilder.DropTable(
                name: "ProjectImportJobs");
        }
    }
}

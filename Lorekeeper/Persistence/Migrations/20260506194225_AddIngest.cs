using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IngestSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    UserInstructions = table.Column<string>(type: "TEXT", nullable: false),
                    SourceText = table.Column<string>(type: "TEXT", nullable: false),
                    SourceHash = table.Column<string>(type: "TEXT", nullable: false),
                    VectorIndexState = table.Column<string>(type: "TEXT", nullable: false),
                    VectorIndexedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VectorIndexError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestSources_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    TotalSourceChunks = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedSourceChunks = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedEntityCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedRelationshipCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", nullable: true),
                    EncodingName = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestJobs_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngestJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestSourceChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    HeadingPath = table.Column<string>(type: "TEXT", nullable: false),
                    StartChar = table.Column<int>(type: "INTEGER", nullable: false),
                    EndChar = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedTokenCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenCountMethod = table.Column<string>(type: "TEXT", nullable: false),
                    TokenEncodingName = table.Column<string>(type: "TEXT", nullable: true),
                    TokenCountIsExact = table.Column<bool>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    AgentNotes = table.Column<string>(type: "TEXT", nullable: false),
                    StructureStatus = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestSourceChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestSourceChunks_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestVectorFragments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    VectorRowId = table.Column<long>(type: "INTEGER", nullable: true),
                    StartChar = table.Column<int>(type: "INTEGER", nullable: false),
                    EndChar = table.Column<int>(type: "INTEGER", nullable: false),
                    Metadata = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestVectorFragments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestVectorFragments_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestJobEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestJobEvents_IngestJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IngestJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestJobChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceChunkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceChunkIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedEntityCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedRelationshipCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestJobChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestJobChunks_IngestJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IngestJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngestJobChunks_IngestSourceChunks_SourceChunkId",
                        column: x => x.SourceChunkId,
                        principalTable: "IngestSourceChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestReportItems",
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

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobChunks_JobId_SourceChunkId",
                table: "IngestJobChunks",
                columns: new[] { "JobId", "SourceChunkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobChunks_JobId_SourceChunkIndex",
                table: "IngestJobChunks",
                columns: new[] { "JobId", "SourceChunkIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobChunks_SourceChunkId",
                table: "IngestJobChunks",
                column: "SourceChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobEvents_JobId_CreatedAt",
                table: "IngestJobEvents",
                columns: new[] { "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_ProjectId_Status_CreatedAt",
                table: "IngestJobs",
                columns: new[] { "ProjectId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestJobs_SourceId_CreatedAt",
                table: "IngestJobs",
                columns: new[] { "SourceId", "CreatedAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourceChunks_SourceId_Index",
                table: "IngestSourceChunks",
                columns: new[] { "SourceId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestSources_ProjectId_CreatedAt",
                table: "IngestSources",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestVectorFragments_SourceId_Index",
                table: "IngestVectorFragments",
                columns: new[] { "SourceId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestVectorFragments_VectorRowId",
                table: "IngestVectorFragments",
                column: "VectorRowId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestJobChunks");

            migrationBuilder.DropTable(
                name: "IngestJobEvents");

            migrationBuilder.DropTable(
                name: "IngestReportItems");

            migrationBuilder.DropTable(
                name: "IngestVectorFragments");

            migrationBuilder.DropTable(
                name: "IngestJobs");

            migrationBuilder.DropTable(
                name: "IngestSourceChunks");

            migrationBuilder.DropTable(
                name: "IngestSources");
        }
    }
}

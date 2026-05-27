using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestStagingRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IngestStagingRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceChunkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceChunkIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GraphNodeId = table.Column<long>(type: "INTEGER", nullable: true),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    AliasesJson = table.Column<string>(type: "TEXT", nullable: false),
                    WikiSectionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    FromEntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToEntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GraphEdgeId = table.Column<long>(type: "INTEGER", nullable: true),
                    EdgeType = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinalizedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestStagingRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestStagingRecords_IngestJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "IngestJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IngestStagingRecords_IngestSourceChunks_SourceChunkId",
                        column: x => x.SourceChunkId,
                        principalTable: "IngestSourceChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IngestStagingRecords_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_EntityId",
                table: "IngestStagingRecords",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_GraphEdgeId",
                table: "IngestStagingRecords",
                column: "GraphEdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_GraphNodeId",
                table: "IngestStagingRecords",
                column: "GraphNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_JobId_Kind_Status_CreatedAt",
                table: "IngestStagingRecords",
                columns: new[] { "JobId", "Kind", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_JobId_SourceChunkId_Kind_Status",
                table: "IngestStagingRecords",
                columns: new[] { "JobId", "SourceChunkId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_SourceChunkId",
                table: "IngestStagingRecords",
                column: "SourceChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestStagingRecords_SourceId_Status",
                table: "IngestStagingRecords",
                columns: new[] { "SourceId", "Status" });

            migrationBuilder.Sql(
                """
                UPDATE "GraphNodes"
                SET "Properties" = json_remove("Properties", '$.ingestSourceAssertionsJson', '$.ingestRelationshipAssertionsJson')
                WHERE "Properties" LIKE '%ingestSourceAssertionsJson%'
                   OR "Properties" LIKE '%ingestRelationshipAssertionsJson%';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "GraphEdges"
                SET "Properties" = json_remove("Properties", '$.ingestSourceAssertionsJson', '$.ingestRelationshipAssertionsJson')
                WHERE "Properties" LIKE '%ingestSourceAssertionsJson%'
                   OR "Properties" LIKE '%ingestRelationshipAssertionsJson%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestStagingRecords");
        }
    }
}

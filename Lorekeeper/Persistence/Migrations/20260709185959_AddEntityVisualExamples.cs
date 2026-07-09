using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityVisualExamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CachedImagesJson",
                table: "WebIngestCandidates",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "EntityVisualTargetsJson",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "InheritSourceEntityTargets",
                table: "ProjectImageGenerationJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "SourceVisualCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IngestSourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WebIngestCandidateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Locator = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    StartChar = table.Column<int>(type: "INTEGER", nullable: true),
                    EndChar = table.Column<int>(type: "INTEGER", nullable: true),
                    PromotedImageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceVisualCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceVisualCandidates_IngestSources_IngestSourceId",
                        column: x => x.IngestSourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceVisualCandidates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceVisualCandidates_PublishAssets_PromotedImageId",
                        column: x => x.PromotedImageId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SourceVisualCandidates_WebIngestCandidates_WebIngestCandidateId",
                        column: x => x.WebIngestCandidateId,
                        principalTable: "WebIngestCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EntityVisualExamples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GraphNodeId = table.Column<long>(type: "INTEGER", nullable: false),
                    ImageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    SourceVisualCandidateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityVisualExamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EntityVisualExamples_GraphNodes_GraphNodeId",
                        column: x => x.GraphNodeId,
                        principalTable: "GraphNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EntityVisualExamples_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EntityVisualExamples_PublishAssets_ImageId",
                        column: x => x.ImageId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EntityVisualExamples_SourceVisualCandidates_SourceVisualCandidateId",
                        column: x => x.SourceVisualCandidateId,
                        principalTable: "SourceVisualCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntityVisualExamples_GraphNodeId_ImageId",
                table: "EntityVisualExamples",
                columns: new[] { "GraphNodeId", "ImageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EntityVisualExamples_ImageId",
                table: "EntityVisualExamples",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_EntityVisualExamples_ProjectId_GraphNodeId_SortOrder",
                table: "EntityVisualExamples",
                columns: new[] { "ProjectId", "GraphNodeId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_EntityVisualExamples_SourceVisualCandidateId",
                table: "EntityVisualExamples",
                column: "SourceVisualCandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceVisualCandidates_IngestSourceId",
                table: "SourceVisualCandidates",
                column: "IngestSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceVisualCandidates_ProjectId_ContentHash",
                table: "SourceVisualCandidates",
                columns: new[] { "ProjectId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceVisualCandidates_ProjectId_Kind_Status_CreatedAt",
                table: "SourceVisualCandidates",
                columns: new[] { "ProjectId", "Kind", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceVisualCandidates_PromotedImageId",
                table: "SourceVisualCandidates",
                column: "PromotedImageId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceVisualCandidates_WebIngestCandidateId",
                table: "SourceVisualCandidates",
                column: "WebIngestCandidateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntityVisualExamples");

            migrationBuilder.DropTable(
                name: "SourceVisualCandidates");

            migrationBuilder.DropColumn(
                name: "CachedImagesJson",
                table: "WebIngestCandidates");

            migrationBuilder.DropColumn(
                name: "EntityVisualTargetsJson",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "InheritSourceEntityTargets",
                table: "ProjectImageGenerationJobs");
        }
    }
}

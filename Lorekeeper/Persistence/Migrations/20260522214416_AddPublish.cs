using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    GenerationModel = table.Column<string>(type: "TEXT", nullable: false),
                    SourceMetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishAssets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishOutlineSelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishOutlineSelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishOutlineSelections_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishImagePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlacementKind = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishImagePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishImagePlacements_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublishImagePlacements_PublishAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TitleOverride = table.Column<string>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", nullable: false),
                    Copyright = table.Column<string>(type: "TEXT", nullable: false),
                    Isbn = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Dedication = table.Column<string>(type: "TEXT", nullable: false),
                    Acknowledgments = table.Column<string>(type: "TEXT", nullable: false),
                    References = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeVisibleTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberActs = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberChapters = table.Column<bool>(type: "INTEGER", nullable: false),
                    SelectedCoverAssetId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishProfiles_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublishProfiles_PublishAssets_SelectedCoverAssetId",
                        column: x => x.SelectedCoverAssetId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublishAssets_ProjectId_CreatedAt",
                table: "PublishAssets",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishImagePlacements_AssetId",
                table: "PublishImagePlacements",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishImagePlacements_ProjectId_TargetKind_TargetId_PlacementKind_SortOrder",
                table: "PublishImagePlacements",
                columns: new[] { "ProjectId", "TargetKind", "TargetId", "PlacementKind", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishOutlineSelections_ProjectId_TargetKind_TargetId",
                table: "PublishOutlineSelections",
                columns: new[] { "ProjectId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishProfiles_ProjectId",
                table: "PublishProfiles",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishProfiles_SelectedCoverAssetId",
                table: "PublishProfiles",
                column: "SelectedCoverAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublishImagePlacements");

            migrationBuilder.DropTable(
                name: "PublishOutlineSelections");

            migrationBuilder.DropTable(
                name: "PublishProfiles");

            migrationBuilder.DropTable(
                name: "PublishAssets");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveImageMasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mask and regional-guide editing is gone. Painted masks were never part of
            // snapshots or exports, so their rows and job links are dropped outright.
            migrationBuilder.DropTable(
                name: "ProjectImageMasks");

            migrationBuilder.DropIndex(
                name: "IX_ProjectImageGenerationJobs_MaskId",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "MaskId",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "SupportsRegionalGuide",
                table: "ImageProviderModels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MaskId",
                table: "ProjectImageGenerationJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsRegionalGuide",
                table: "ImageProviderModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProjectImageMasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKind = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectImageMasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectImageMasks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectImageMasks_PublishAssets_ImageId",
                        column: x => x.ImageId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageGenerationJobs_MaskId",
                table: "ProjectImageGenerationJobs",
                column: "MaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMasks_ImageId",
                table: "ProjectImageMasks",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMasks_ProjectId_ImageId_CreatedAt",
                table: "ProjectImageMasks",
                columns: new[] { "ProjectId", "ImageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageMasks_ProjectId_OwnerKind_OwnerId",
                table: "ProjectImageMasks",
                columns: new[] { "ProjectId", "OwnerKind", "OwnerId" });
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdaptiveArtifactIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastVisionTestAuthType",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastVisionTestCredentialSourceId",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastVisionTestEndpointUrl",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastVisionTestError",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastVisionTestModelId",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastVisionTestSucceeded",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVisionTestedAt",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IngestSourcePages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    StartChar = table.Column<int>(type: "INTEGER", nullable: false),
                    EndChar = table.Column<int>(type: "INTEGER", nullable: false),
                    ExtractionMethod = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageHash = table.Column<string>(type: "TEXT", nullable: false),
                    RenderSettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    VisionProviderId = table.Column<int>(type: "INTEGER", nullable: true),
                    VisionModelName = table.Column<string>(type: "TEXT", nullable: false),
                    Diagnostics = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestSourcePages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestSourcePages_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestSourceBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourcePageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Locator = table.Column<string>(type: "TEXT", nullable: false),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    StartChar = table.Column<int>(type: "INTEGER", nullable: false),
                    EndChar = table.Column<int>(type: "INTEGER", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestSourceBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestSourceBlocks_IngestSourcePages_SourcePageId",
                        column: x => x.SourcePageId,
                        principalTable: "IngestSourcePages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IngestSourceBlocks_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourceBlocks_SourceId_Index",
                table: "IngestSourceBlocks",
                columns: new[] { "SourceId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourceBlocks_SourceId_StartChar",
                table: "IngestSourceBlocks",
                columns: new[] { "SourceId", "StartChar" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourceBlocks_SourcePageId",
                table: "IngestSourceBlocks",
                column: "SourcePageId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourcePages_SourceId_PageNumber",
                table: "IngestSourcePages",
                columns: new[] { "SourceId", "PageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourcePages_SourceId_StartChar",
                table: "IngestSourcePages",
                columns: new[] { "SourceId", "StartChar" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestSourceBlocks");

            migrationBuilder.DropTable(
                name: "IngestSourcePages");

            migrationBuilder.DropColumn(
                name: "LastVisionTestAuthType",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestCredentialSourceId",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestEndpointUrl",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestError",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestModelId",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestSucceeded",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestedAt",
                table: "LlmProviders");
        }
    }
}

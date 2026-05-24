using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmbeddingConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApiKind = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestedProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestedApiKind = table.Column<string>(type: "TEXT", nullable: false),
                    LastTestedModelId = table.Column<string>(type: "TEXT", nullable: false),
                    LastTestedDimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmbeddingConfigurations_LlmProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "LlmProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingConfigurations_ProviderId",
                table: "EmbeddingConfigurations",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmbeddingConfigurations");
        }
    }
}

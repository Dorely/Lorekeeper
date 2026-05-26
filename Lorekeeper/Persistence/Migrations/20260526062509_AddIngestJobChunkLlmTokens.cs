using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestJobChunkLlmTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LlmTokenCount",
                table: "IngestJobChunks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LlmTokenCountIsExact",
                table: "IngestJobChunks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LlmTokenCountMethod",
                table: "IngestJobChunks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LlmTokenEncodingName",
                table: "IngestJobChunks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LlmTokenCount",
                table: "IngestJobChunks");

            migrationBuilder.DropColumn(
                name: "LlmTokenCountIsExact",
                table: "IngestJobChunks");

            migrationBuilder.DropColumn(
                name: "LlmTokenCountMethod",
                table: "IngestJobChunks");

            migrationBuilder.DropColumn(
                name: "LlmTokenEncodingName",
                table: "IngestJobChunks");
        }
    }
}

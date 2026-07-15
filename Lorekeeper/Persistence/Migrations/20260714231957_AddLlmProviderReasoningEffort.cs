using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmProviderReasoningEffort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastChatTestReasoningEffort",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastVisionTestReasoningEffort",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastChatTestReasoningEffort",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastVisionTestReasoningEffort",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "LlmProviders");
        }
    }
}

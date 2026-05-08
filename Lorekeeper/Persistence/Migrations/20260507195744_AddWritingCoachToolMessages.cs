using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWritingCoachToolMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ToolCallId",
                table: "WritingCoachMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolCallsJson",
                table: "WritingCoachMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ToolName",
                table: "WritingCoachMessages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ToolCallId",
                table: "WritingCoachMessages");

            migrationBuilder.DropColumn(
                name: "ToolCallsJson",
                table: "WritingCoachMessages");

            migrationBuilder.DropColumn(
                name: "ToolName",
                table: "WritingCoachMessages");
        }
    }
}

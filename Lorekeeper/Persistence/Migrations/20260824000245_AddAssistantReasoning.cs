using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantReasoning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "WritingCoachMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "ResearchMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "PublishMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "ProjectImageMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "OutlineMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reasoning",
                table: "EditorMessages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "WritingCoachMessages");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "ResearchMessages");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "PublishMessages");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "ProjectImageMessages");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "OutlineMessages");

            migrationBuilder.DropColumn(
                name: "Reasoning",
                table: "EditorMessages");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveChatResponseProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "WritingCoachMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "ResearchMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "PublishMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "ProjectImageMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "OutlineMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "EditorRevisionMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "EditorMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseMetadataJson",
                table: "ContestCandidates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "WritingCoachMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "ResearchMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "PublishMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "ProjectImageMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "OutlineMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "EditorRevisionMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "EditorMessages");

            migrationBuilder.DropColumn(
                name: "ResponseMetadataJson",
                table: "ContestCandidates");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveContestBriefFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MutationInstructions",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "OperationKind",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "TargetRangesJson",
                table: "ContestBatches");

            migrationBuilder.DropColumn(
                name: "UserGoal",
                table: "ContestBatches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MutationInstructions",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OperationKind",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetRangesJson",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UserGoal",
                table: "ContestBatches",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}

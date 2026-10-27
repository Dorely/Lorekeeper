using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteExternalWorkspaceContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointRequestKey",
                table: "ExternalWorks");

            migrationBuilder.DropColumn(
                name: "CompletionError",
                table: "ExternalWorks");

            migrationBuilder.AddColumn<string>(
                name: "ClientKind",
                table: "McpClientGrants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IdentitiesJson",
                table: "ExternalTransferCaptures",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientKind",
                table: "McpClientGrants");

            migrationBuilder.DropColumn(
                name: "IdentitiesJson",
                table: "ExternalTransferCaptures");

            migrationBuilder.AddColumn<string>(
                name: "CheckpointRequestKey",
                table: "ExternalWorks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletionError",
                table: "ExternalWorks",
                type: "TEXT",
                nullable: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalProjectionRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ClientKind",
                table: "McpClientGrants",
                type: "TEXT",
                nullable: false,
                defaultValue: "Other MCP client",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "IdentitiesJson",
                table: "ExternalTransferCaptures",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<DateTime>(
                name: "RepairAttemptedAt",
                table: "ExternalMutationReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RepairPending",
                table: "ExternalMutationReceipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RepairTargetsJson",
                table: "ExternalMutationReceipts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE ExternalTransferCaptures SET IdentitiesJson = '[]' WHERE trim(IdentitiesJson) = '';");
            migrationBuilder.Sql("UPDATE McpClientGrants SET ClientKind = 'Other MCP client' WHERE trim(ClientKind) = '';");
            migrationBuilder.Sql("UPDATE ExternalMutationReceipts SET RepairTargetsJson = '[]' WHERE trim(RepairTargetsJson) = '';");
            migrationBuilder.Sql("""
                UPDATE ExternalMutationReceipts
                SET ProjectId = upper(json_extract(ResultJson, '$.result.id'))
                WHERE Operation = 'create_project' AND ProjectId IS NULL AND json_valid(ResultJson)
                    AND json_extract(ResultJson, '$.result.id') IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepairAttemptedAt",
                table: "ExternalMutationReceipts");

            migrationBuilder.DropColumn(
                name: "RepairPending",
                table: "ExternalMutationReceipts");

            migrationBuilder.DropColumn(
                name: "RepairTargetsJson",
                table: "ExternalMutationReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "ClientKind",
                table: "McpClientGrants",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "Other MCP client");

            migrationBuilder.AlterColumn<string>(
                name: "IdentitiesJson",
                table: "ExternalTransferCaptures",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "[]");
        }
    }
}

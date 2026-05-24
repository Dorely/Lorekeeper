using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmProviderChatReadiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastChatTestAuthType",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastChatTestCredentialSourceId",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastChatTestEndpointUrl",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastChatTestError",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastChatTestModelId",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastChatTestSucceeded",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastChatTestedAt",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastChatTestAuthType",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastChatTestCredentialSourceId",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastChatTestEndpointUrl",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastChatTestError",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastChatTestModelId",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastChatTestSucceeded",
                table: "LlmProviders");

            migrationBuilder.DropColumn(
                name: "LastChatTestedAt",
                table: "LlmProviders");
        }
    }
}

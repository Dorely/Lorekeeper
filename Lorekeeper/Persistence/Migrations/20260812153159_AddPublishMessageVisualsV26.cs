using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishMessageVisualsV26 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublishMessageVisuals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRefId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishMessageVisuals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishMessageVisuals_PublishMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "PublishMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublishMessageVisuals_MessageId_SortOrder",
                table: "PublishMessageVisuals",
                columns: new[] { "MessageId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishMessageVisuals_ToolCallId_CreatedAt",
                table: "PublishMessageVisuals",
                columns: new[] { "ToolCallId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublishMessageVisuals");
        }
    }
}

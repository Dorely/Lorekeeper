using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageImageAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatMessageImageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Surface = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessageImageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessageImageAttachments_PublishAssets_ImageId",
                        column: x => x.ImageId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageImageAttachments_ImageId",
                table: "ChatMessageImageAttachments",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageImageAttachments_ProjectId_Surface_MessageId_SortOrder",
                table: "ChatMessageImageAttachments",
                columns: new[] { "ProjectId", "Surface", "MessageId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageImageAttachments_Surface_MessageId_ImageId",
                table: "ChatMessageImageAttachments",
                columns: new[] { "Surface", "MessageId", "ImageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessageImageAttachments");
        }
    }
}

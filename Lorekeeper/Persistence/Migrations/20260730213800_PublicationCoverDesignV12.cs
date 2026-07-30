using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicationCoverDesignV12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicationCoverDesigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    SpineText = table.Column<string>(type: "TEXT", nullable: false),
                    BackCopy = table.Column<string>(type: "TEXT", nullable: false),
                    BackgroundColor = table.Column<string>(type: "TEXT", nullable: false),
                    BarcodeMode = table.Column<string>(type: "TEXT", nullable: false),
                    ImageFocalXPercent = table.Column<double>(type: "REAL", nullable: false),
                    ImageFocalYPercent = table.Column<double>(type: "REAL", nullable: false),
                    AcknowledgedTemplateFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationCoverDesigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationCoverDesigns_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationCoverDesigns_EditionId",
                table: "PublicationCoverDesigns",
                column: "EditionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationCoverDesigns");
        }
    }
}

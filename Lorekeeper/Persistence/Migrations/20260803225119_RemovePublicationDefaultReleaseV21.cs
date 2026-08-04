using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemovePublicationDefaultReleaseV21 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PublicationEditions_ProjectId_IsDefault",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "PublicationEditions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "PublicationEditions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditions_ProjectId_IsDefault",
                table: "PublicationEditions",
                columns: new[] { "ProjectId", "IsDefault" },
                unique: true,
                filter: "\"IsDefault\" = 1");
        }
    }
}

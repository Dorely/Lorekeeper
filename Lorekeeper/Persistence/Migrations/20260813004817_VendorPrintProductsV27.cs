using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VendorPrintProductsV27 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenericPrintTemplateJson",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrintFinish",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "Matte");

            migrationBuilder.AddColumn<string>(
                name: "PrintProductKey",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrintRegistryVersion",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrintCoverMode",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SurfaceScenesJson",
                table: "PublicationCoverDesigns",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenericPrintTemplateJson",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintCoverMode",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintFinish",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintProductKey",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "PrintRegistryVersion",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "SurfaceScenesJson",
                table: "PublicationCoverDesigns");
        }
    }
}

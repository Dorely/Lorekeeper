using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeletePartialsWithFinalImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectImagePartials_PublishAssets_FinalOutputImageId",
                table: "ProjectImagePartials");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectImagePartials_PublishAssets_FinalOutputImageId",
                table: "ProjectImagePartials",
                column: "FinalOutputImageId",
                principalTable: "PublishAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectImagePartials_PublishAssets_FinalOutputImageId",
                table: "ProjectImagePartials");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectImagePartials_PublishAssets_FinalOutputImageId",
                table: "ProjectImagePartials",
                column: "FinalOutputImageId",
                principalTable: "PublishAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

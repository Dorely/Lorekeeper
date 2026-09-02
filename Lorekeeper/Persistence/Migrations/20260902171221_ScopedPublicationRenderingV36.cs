using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedPublicationRenderingV36 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_RenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.RenameColumn(
                name: "RenderJobId",
                table: "PublicationPreparationJobs",
                newName: "InteriorRenderJobId");

            migrationBuilder.RenameIndex(
                name: "IX_PublicationPreparationJobs_RenderJobId",
                table: "PublicationPreparationJobs",
                newName: "IX_PublicationPreparationJobs_InteriorRenderJobId");

            migrationBuilder.AddColumn<int>(
                name: "InteriorPageCount",
                table: "PublicationRenderJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "PublicationRenderJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "Book");

            migrationBuilder.AddColumn<Guid>(
                name: "BookRenderJobId",
                table: "PublicationPreparationJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CoverRenderJobId",
                table: "PublicationPreparationJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE PublicationPreparationJobs
                SET BookRenderJobId = InteriorRenderJobId
                WHERE EditionId IS NULL
                   OR EditionId IN (
                       SELECT Id
                       FROM PublicationEditions
                       WHERE Format IN ('Epub', 'DigitalPdf'));

                UPDATE PublicationPreparationJobs
                SET InteriorRenderJobId = NULL;

                UPDATE PublicationArtifacts
                SET IsLegacy = 1
                WHERE RenderJobId IN (
                    SELECT jobs.Id
                    FROM PublicationRenderJobs AS jobs
                    INNER JOIN PublicationEditions AS editions ON editions.Id = jobs.EditionId
                    WHERE editions.Format IN ('Paperback', 'Hardcover'));

                UPDATE PublicationRenderJobs
                SET IsLegacy = 1
                WHERE EditionId IN (
                    SELECT Id
                    FROM PublicationEditions
                    WHERE Format IN ('Paperback', 'Hardcover'));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPreparationJobs_BookRenderJobId",
                table: "PublicationPreparationJobs",
                column: "BookRenderJobId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationPreparationJobs_CoverRenderJobId",
                table: "PublicationPreparationJobs",
                column: "CoverRenderJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_BookRenderJobId",
                table: "PublicationPreparationJobs",
                column: "BookRenderJobId",
                principalTable: "PublicationRenderJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_CoverRenderJobId",
                table: "PublicationPreparationJobs",
                column: "CoverRenderJobId",
                principalTable: "PublicationRenderJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_InteriorRenderJobId",
                table: "PublicationPreparationJobs",
                column: "InteriorRenderJobId",
                principalTable: "PublicationRenderJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE PublicationPreparationJobs
                SET InteriorRenderJobId = COALESCE(
                    InteriorRenderJobId,
                    CoverRenderJobId,
                    BookRenderJobId);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_BookRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_CoverRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_InteriorRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.DropIndex(
                name: "IX_PublicationPreparationJobs_BookRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.DropIndex(
                name: "IX_PublicationPreparationJobs_CoverRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.DropColumn(
                name: "InteriorPageCount",
                table: "PublicationRenderJobs");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "PublicationRenderJobs");

            migrationBuilder.DropColumn(
                name: "BookRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.DropColumn(
                name: "CoverRenderJobId",
                table: "PublicationPreparationJobs");

            migrationBuilder.RenameColumn(
                name: "InteriorRenderJobId",
                table: "PublicationPreparationJobs",
                newName: "RenderJobId");

            migrationBuilder.RenameIndex(
                name: "IX_PublicationPreparationJobs_InteriorRenderJobId",
                table: "PublicationPreparationJobs",
                newName: "IX_PublicationPreparationJobs_RenderJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationPreparationJobs_PublicationRenderJobs_RenderJobId",
                table: "PublicationPreparationJobs",
                column: "RenderJobId",
                principalTable: "PublicationRenderJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

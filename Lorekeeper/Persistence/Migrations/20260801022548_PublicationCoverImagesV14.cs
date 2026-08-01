using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicationCoverImagesV14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SelectedCoverImageId",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "PublicationEditions" AS source
                SET "SelectedCoverImageId" = (
                    SELECT MIN(asset."Id")
                    FROM "Chapters" AS chapter
                    JOIN json_each(
                        CASE WHEN json_valid(chapter."PageLayoutJson")
                            THEN chapter."PageLayoutJson"
                            ELSE '{"images":[]}'
                        END,
                        '$.images') AS element
                    JOIN "PublishAssets" AS asset
                      ON lower(asset."Id") = lower(json_extract(
                            CASE WHEN json_valid(element.value)
                                THEN element.value
                                ELSE '{}'
                            END,
                            '$.imageId'))
                     AND asset."ProjectId" = source."ProjectId"
                    WHERE chapter."Id" = source."SelectedCoverChapterId"
                      AND chapter."ProjectId" = source."ProjectId"
                    GROUP BY chapter."Id"
                    HAVING COUNT(*) > 0
                       AND COUNT(DISTINCT lower(asset."Id")) = 1
                       AND COUNT(*) = (
                           SELECT COUNT(*)
                           FROM json_each(
                               CASE WHEN json_valid(chapter."PageLayoutJson")
                                   THEN chapter."PageLayoutJson"
                                   ELSE '{"images":[]}'
                               END,
                               '$.images')
                       )
                );

                INSERT INTO "PublicationEditionOutlineItems" (
                    "Id", "EditionId", "TargetKind", "TargetId", "ActId", "ChapterId",
                    "IsIncluded", "SortOrder", "CreatedAt", "UpdatedAt")
                SELECT
                    lower(
                        hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' ||
                        hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' ||
                        hex(randomblob(6))),
                    source."Id", 'Chapter', source."SelectedCoverChapterId", NULL,
                    source."SelectedCoverChapterId", 0,
                    COALESCE((
                        SELECT MAX(existing."SortOrder") + 1
                        FROM "PublicationEditionOutlineItems" AS existing
                        WHERE existing."EditionId" = source."Id"
                    ), 0),
                    source."UpdatedAt", source."UpdatedAt"
                FROM "PublicationEditions" AS source
                WHERE source."SelectedCoverChapterId" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "PublicationEditionOutlineItems" AS existing
                      WHERE existing."EditionId" = source."Id"
                        AND existing."TargetKind" = 'Chapter'
                        AND lower(existing."TargetId") = lower(source."SelectedCoverChapterId")
                  );

                UPDATE "PublicationEditionOutlineItems" AS outline
                SET "IsIncluded" = 0
                WHERE outline."TargetKind" = 'Chapter'
                  AND EXISTS (
                      SELECT 1
                      FROM "PublicationEditions" AS source
                      WHERE source."Id" = outline."EditionId"
                        AND source."SelectedCoverChapterId" IS NOT NULL
                        AND lower(outline."TargetId") = lower(source."SelectedCoverChapterId")
                  );
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationEditions_Chapters_SelectedCoverChapterId",
                table: "PublicationEditions");

            migrationBuilder.DropIndex(
                name: "IX_PublicationEditions_SelectedCoverChapterId",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "SelectedCoverChapterId",
                table: "PublicationEditions");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditions_SelectedCoverImageId",
                table: "PublicationEditions",
                column: "SelectedCoverImageId");

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationEditions_PublishAssets_SelectedCoverImageId",
                table: "PublicationEditions",
                column: "SelectedCoverImageId",
                principalTable: "PublishAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SelectedCoverChapterId",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.DropForeignKey(
                name: "FK_PublicationEditions_PublishAssets_SelectedCoverImageId",
                table: "PublicationEditions");

            migrationBuilder.DropIndex(
                name: "IX_PublicationEditions_SelectedCoverImageId",
                table: "PublicationEditions");

            migrationBuilder.DropColumn(
                name: "SelectedCoverImageId",
                table: "PublicationEditions");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditions_SelectedCoverChapterId",
                table: "PublicationEditions",
                column: "SelectedCoverChapterId");

            migrationBuilder.AddForeignKey(
                name: "FK_PublicationEditions_Chapters_SelectedCoverChapterId",
                table: "PublicationEditions",
                column: "SelectedCoverChapterId",
                principalTable: "Chapters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

public partial class AddProjectImageCrops : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            PRAGMA foreign_keys=OFF;
            BEGIN TRANSACTION;

            CREATE TABLE "__ef_temp_PublishAssets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PublishAssets" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Data" BLOB NOT NULL,
                "AltText" TEXT NOT NULL,
                "Prompt" TEXT NOT NULL,
                "GenerationModel" TEXT NOT NULL,
                "SourceMetadataJson" TEXT NOT NULL,
                "DerivedFromImageId" TEXT NULL,
                "CropXPercent" REAL NULL,
                "CropYPercent" REAL NULL,
                "CropWidthPercent" REAL NULL,
                "CropHeightPercent" REAL NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_PublishAssets_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_PublishAssets_PublishAssets_DerivedFromImageId" FOREIGN KEY ("DerivedFromImageId") REFERENCES "PublishAssets" ("Id") ON DELETE SET NULL
            );

            INSERT INTO "__ef_temp_PublishAssets" (
                "Id", "ProjectId", "Source", "FileName", "ContentType", "Data", "AltText", "Prompt",
                "GenerationModel", "SourceMetadataJson", "DerivedFromImageId", "CropXPercent", "CropYPercent",
                "CropWidthPercent", "CropHeightPercent", "CreatedAt", "UpdatedAt")
            SELECT
                "Id", "ProjectId", "Source", "FileName", "ContentType", "Data", "AltText", "Prompt",
                "GenerationModel", "SourceMetadataJson", NULL, NULL, NULL, NULL, NULL, "CreatedAt", "UpdatedAt"
            FROM "PublishAssets";

            DROP TABLE "PublishAssets";
            ALTER TABLE "__ef_temp_PublishAssets" RENAME TO "PublishAssets";

            CREATE INDEX "IX_PublishAssets_DerivedFromImageId" ON "PublishAssets" ("DerivedFromImageId");
            CREATE UNIQUE INDEX "IX_PublishAssets_DerivedFromImageId_CropXPercent_CropYPercent_CropWidthPercent_CropHeightPercent"
                ON "PublishAssets" ("DerivedFromImageId", "CropXPercent", "CropYPercent", "CropWidthPercent", "CropHeightPercent");
            CREATE INDEX "IX_PublishAssets_ProjectId_CreatedAt" ON "PublishAssets" ("ProjectId", "CreatedAt");

            COMMIT;
            PRAGMA foreign_keys=ON;
            """,
            suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            PRAGMA foreign_keys=OFF;
            BEGIN TRANSACTION;

            CREATE TABLE "__ef_temp_PublishAssets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PublishAssets" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "Data" BLOB NOT NULL,
                "AltText" TEXT NOT NULL,
                "Prompt" TEXT NOT NULL,
                "GenerationModel" TEXT NOT NULL,
                "SourceMetadataJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "FK_PublishAssets_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
            );

            INSERT INTO "__ef_temp_PublishAssets" (
                "Id", "ProjectId", "Source", "FileName", "ContentType", "Data", "AltText", "Prompt",
                "GenerationModel", "SourceMetadataJson", "CreatedAt", "UpdatedAt")
            SELECT
                "Id", "ProjectId", "Source", "FileName", "ContentType", "Data", "AltText", "Prompt",
                "GenerationModel", "SourceMetadataJson", "CreatedAt", "UpdatedAt"
            FROM "PublishAssets";

            DROP TABLE "PublishAssets";
            ALTER TABLE "__ef_temp_PublishAssets" RENAME TO "PublishAssets";
            CREATE INDEX "IX_PublishAssets_ProjectId_CreatedAt" ON "PublishAssets" ("ProjectId", "CreatedAt");

            COMMIT;
            PRAGMA foreign_keys=ON;
            """,
            suppressTransaction: true);
    }
}

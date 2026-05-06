using System;
using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260506211500_AddIngestJobProvider")]
    public partial class AddIngestJobProvider : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys=OFF;
                BEGIN TRANSACTION;

                CREATE TABLE "__ef_temp_IngestJobs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_IngestJobs" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "SourceId" TEXT NOT NULL,
                    "Instructions" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "TotalSourceChunks" INTEGER NOT NULL,
                    "CompletedSourceChunks" INTEGER NOT NULL,
                    "CreatedEntityCount" INTEGER NOT NULL,
                    "CreatedRelationshipCount" INTEGER NOT NULL,
                    "CurrentMessage" TEXT NULL,
                    "ErrorMessage" TEXT NULL,
                    "ProviderId" INTEGER NULL,
                    "ModelName" TEXT NULL,
                    "EncodingName" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "StartedAt" TEXT NULL,
                    "CompletedAt" TEXT NULL,
                    CONSTRAINT "FK_IngestJobs_IngestSources_SourceId" FOREIGN KEY ("SourceId") REFERENCES "IngestSources" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_IngestJobs_LlmProviders_ProviderId" FOREIGN KEY ("ProviderId") REFERENCES "LlmProviders" ("Id") ON DELETE SET NULL,
                    CONSTRAINT "FK_IngestJobs_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
                );

                INSERT INTO "__ef_temp_IngestJobs" (
                    "Id", "ProjectId", "SourceId", "Instructions", "Status", "TotalSourceChunks",
                    "CompletedSourceChunks", "CreatedEntityCount", "CreatedRelationshipCount", "CurrentMessage",
                    "ErrorMessage", "ProviderId", "ModelName", "EncodingName", "CreatedAt", "UpdatedAt",
                    "StartedAt", "CompletedAt")
                SELECT
                    "Id", "ProjectId", "SourceId", "Instructions", "Status", "TotalSourceChunks",
                    "CompletedSourceChunks", "CreatedEntityCount", "CreatedRelationshipCount", "CurrentMessage",
                    "ErrorMessage", NULL, "ModelName", "EncodingName", "CreatedAt", "UpdatedAt",
                    "StartedAt", "CompletedAt"
                FROM "IngestJobs";

                DROP TABLE "IngestJobs";
                ALTER TABLE "__ef_temp_IngestJobs" RENAME TO "IngestJobs";

                CREATE INDEX "IX_IngestJobs_ProjectId_Status_CreatedAt" ON "IngestJobs" ("ProjectId", "Status", "CreatedAt");
                CREATE INDEX "IX_IngestJobs_ProviderId" ON "IngestJobs" ("ProviderId");
                CREATE INDEX "IX_IngestJobs_SourceId_CreatedAt" ON "IngestJobs" ("SourceId", "CreatedAt");

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

                CREATE TABLE "__ef_temp_IngestJobs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_IngestJobs" PRIMARY KEY,
                    "ProjectId" TEXT NOT NULL,
                    "SourceId" TEXT NOT NULL,
                    "Instructions" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "TotalSourceChunks" INTEGER NOT NULL,
                    "CompletedSourceChunks" INTEGER NOT NULL,
                    "CreatedEntityCount" INTEGER NOT NULL,
                    "CreatedRelationshipCount" INTEGER NOT NULL,
                    "CurrentMessage" TEXT NULL,
                    "ErrorMessage" TEXT NULL,
                    "ModelName" TEXT NULL,
                    "EncodingName" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "StartedAt" TEXT NULL,
                    "CompletedAt" TEXT NULL,
                    CONSTRAINT "FK_IngestJobs_IngestSources_SourceId" FOREIGN KEY ("SourceId") REFERENCES "IngestSources" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_IngestJobs_Projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE
                );

                INSERT INTO "__ef_temp_IngestJobs" (
                    "Id", "ProjectId", "SourceId", "Instructions", "Status", "TotalSourceChunks",
                    "CompletedSourceChunks", "CreatedEntityCount", "CreatedRelationshipCount", "CurrentMessage",
                    "ErrorMessage", "ModelName", "EncodingName", "CreatedAt", "UpdatedAt", "StartedAt", "CompletedAt")
                SELECT
                    "Id", "ProjectId", "SourceId", "Instructions", "Status", "TotalSourceChunks",
                    "CompletedSourceChunks", "CreatedEntityCount", "CreatedRelationshipCount", "CurrentMessage",
                    "ErrorMessage", "ModelName", "EncodingName", "CreatedAt", "UpdatedAt", "StartedAt", "CompletedAt"
                FROM "IngestJobs";

                DROP TABLE "IngestJobs";
                ALTER TABLE "__ef_temp_IngestJobs" RENAME TO "IngestJobs";

                CREATE INDEX "IX_IngestJobs_ProjectId_Status_CreatedAt" ON "IngestJobs" ("ProjectId", "Status", "CreatedAt");
                CREATE INDEX "IX_IngestJobs_SourceId_CreatedAt" ON "IngestJobs" ("SourceId", "CreatedAt");

                COMMIT;
                PRAGMA foreign_keys=ON;
                """,
                suppressTransaction: true);
        }
    }
}
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpenAiAccountOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__OpenAiAccountOwnershipGuard" (
                    "Value" INTEGER NOT NULL CHECK ("Value" = 0)
                );

                INSERT INTO "__OpenAiAccountOwnershipGuard" ("Value")
                SELECT CASE WHEN
                    EXISTS (
                        SELECT 1
                        FROM "LlmProviders" AS p
                        WHERE p."Name" = 'openai-codex'
                          AND p."AuthType" = 'OAuth'
                          AND p."CredentialSourceId" IS NOT NULL
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM "OAuthTokens" AS t
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM "LlmProviders" AS root
                            WHERE root."Id" = t."ProviderId"
                              AND root."Name" = 'openai-codex'
                              AND root."AuthType" = 'OAuth'
                              AND root."CredentialSourceId" IS NULL
                        )
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM "OAuthTokens"
                        GROUP BY "ProviderId"
                        HAVING COUNT(*) > 1
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM "LlmProviders" AS p
                        WHERE p."AuthType" = 'OAuth'
                          AND NOT EXISTS (
                              SELECT 1
                              FROM "LlmProviders" AS root
                              WHERE root."Name" = 'openai-codex'
                                AND root."AuthType" = 'OAuth'
                                AND root."CredentialSourceId" IS NULL
                                AND (p."Id" = root."Id" OR p."CredentialSourceId" = root."Id")
                          )
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM "LlmProviders" AS p
                        WHERE NULLIF(TRIM(p."ApiKey"), '') IS NOT NULL
                          AND EXISTS (
                              SELECT 1
                              FROM "LlmProviders" AS root
                              WHERE root."Name" = 'openai-codex'
                                AND root."AuthType" = 'OAuth'
                                AND root."CredentialSourceId" IS NULL
                                AND (p."Id" = root."Id" OR p."CredentialSourceId" = root."Id")
                          )
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM "LlmProviders" AS grandchild
                        WHERE grandchild."CredentialSourceId" IN (
                            SELECT child."Id"
                            FROM "LlmProviders" AS child
                            JOIN "LlmProviders" AS root ON child."CredentialSourceId" = root."Id"
                            WHERE root."Name" = 'openai-codex'
                              AND root."AuthType" = 'OAuth'
                              AND root."CredentialSourceId" IS NULL
                        )
                    )
                THEN 1 ELSE 0 END;

                DROP TABLE "__OpenAiAccountOwnershipGuard";
                """);

            migrationBuilder.CreateTable(
                name: "OpenAiAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "TEXT", nullable: true),
                    RequiresReauthenticationAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAuthenticationError = table.Column<string>(type: "TEXT", nullable: true),
                    LastCatalogRefreshAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCatalogRefreshError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenAiAccounts", x => x.Id);
                });

            migrationBuilder.AddColumn<string>(
                name: "AccountAvailability",
                table: "LlmProviders",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTime>(
                name: "AccountAvailabilityCheckedAt",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountAvailabilityError",
                table: "LlmProviders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveredContextWindowTokens",
                table: "LlmProviders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelOrigin",
                table: "LlmProviders",
                type: "TEXT",
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.Sql(
                """
                ALTER TABLE "LlmProviders"
                ADD COLUMN "OpenAiAccountId" INTEGER NULL
                    REFERENCES "OpenAiAccounts" ("Id") ON DELETE RESTRICT;

                INSERT INTO "OpenAiAccounts" (
                    "Id", "DisplayName", "ExternalAccountId", "RequiresReauthenticationAt",
                    "LastAuthenticationError", "LastCatalogRefreshAt", "LastCatalogRefreshError",
                    "CreatedAt", "UpdatedAt")
                SELECT root."Id",
                       COALESCE(NULLIF(TRIM(root."DisplayName"), ''), 'OpenAI account'),
                       NULL, NULL, NULL, NULL, NULL,
                       root."CreatedAt", root."UpdatedAt"
                FROM "LlmProviders" AS root
                WHERE root."Name" = 'openai-codex'
                  AND root."AuthType" = 'OAuth'
                  AND root."CredentialSourceId" IS NULL;

                UPDATE "LlmProviders"
                SET "OpenAiAccountId" = CASE
                        WHEN "Name" = 'openai-codex'
                         AND "AuthType" = 'OAuth'
                         AND "CredentialSourceId" IS NULL
                        THEN "Id"
                        ELSE "CredentialSourceId"
                    END,
                    "CredentialSourceId" = NULL
                WHERE EXISTS (
                    SELECT 1
                    FROM "LlmProviders" AS root
                    WHERE root."Name" = 'openai-codex'
                      AND root."AuthType" = 'OAuth'
                      AND root."CredentialSourceId" IS NULL
                      AND ("LlmProviders"."Id" = root."Id"
                           OR "LlmProviders"."CredentialSourceId" = root."Id")
                );

                CREATE TABLE "__OAuthTokens_OpenAiAccount" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_OAuthTokens" PRIMARY KEY AUTOINCREMENT,
                    "OpenAiAccountId" INTEGER NOT NULL,
                    "AccessToken" TEXT NOT NULL,
                    "RefreshToken" TEXT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "Scope" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_OAuthTokens_OpenAiAccounts_OpenAiAccountId"
                        FOREIGN KEY ("OpenAiAccountId") REFERENCES "OpenAiAccounts" ("Id")
                        ON DELETE CASCADE
                );

                INSERT INTO "__OAuthTokens_OpenAiAccount" (
                    "Id", "OpenAiAccountId", "AccessToken", "RefreshToken", "ExpiresAt", "Scope", "CreatedAt")
                SELECT "Id", "ProviderId", "AccessToken", "RefreshToken", "ExpiresAt", "Scope", "CreatedAt"
                FROM "OAuthTokens";

                DROP TABLE "OAuthTokens";
                ALTER TABLE "__OAuthTokens_OpenAiAccount" RENAME TO "OAuthTokens";
                CREATE INDEX "IX_OAuthTokens_OpenAiAccountId" ON "OAuthTokens" ("OpenAiAccountId");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_LlmProviders_OpenAiAccountId",
                table: "LlmProviders",
                column: "OpenAiAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenAiAccounts_DisplayName",
                table: "OpenAiAccounts",
                column: "DisplayName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "LlmProviders"
                SET "CredentialSourceId" = "OpenAiAccountId"
                WHERE "OpenAiAccountId" IS NOT NULL
                  AND "Id" <> "OpenAiAccountId";

                CREATE TABLE "__OAuthTokens_LlmProvider" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_OAuthTokens" PRIMARY KEY AUTOINCREMENT,
                    "ProviderId" INTEGER NOT NULL,
                    "AccessToken" TEXT NOT NULL,
                    "RefreshToken" TEXT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "Scope" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_OAuthTokens_LlmProviders_ProviderId"
                        FOREIGN KEY ("ProviderId") REFERENCES "LlmProviders" ("Id")
                        ON DELETE CASCADE
                );

                INSERT INTO "__OAuthTokens_LlmProvider" (
                    "Id", "ProviderId", "AccessToken", "RefreshToken", "ExpiresAt", "Scope", "CreatedAt")
                SELECT "Id", "OpenAiAccountId", "AccessToken", "RefreshToken", "ExpiresAt", "Scope", "CreatedAt"
                FROM "OAuthTokens";

                DROP TABLE "OAuthTokens";
                ALTER TABLE "__OAuthTokens_LlmProvider" RENAME TO "OAuthTokens";
                CREATE INDEX "IX_OAuthTokens_ProviderId" ON "OAuthTokens" ("ProviderId");

                DROP INDEX "IX_LlmProviders_OpenAiAccountId";
                DROP INDEX "IX_OpenAiAccounts_DisplayName";

                ALTER TABLE "LlmProviders" DROP COLUMN "OpenAiAccountId";
                ALTER TABLE "LlmProviders" DROP COLUMN "AccountAvailability";
                ALTER TABLE "LlmProviders" DROP COLUMN "AccountAvailabilityCheckedAt";
                ALTER TABLE "LlmProviders" DROP COLUMN "AccountAvailabilityError";
                ALTER TABLE "LlmProviders" DROP COLUMN "DiscoveredContextWindowTokens";
                ALTER TABLE "LlmProviders" DROP COLUMN "ModelOrigin";

                DROP TABLE "OpenAiAccounts";
                """);
        }
    }
}

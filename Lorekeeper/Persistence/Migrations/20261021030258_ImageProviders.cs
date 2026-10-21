using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImageProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImageProviderId",
                table: "ProjectImageGenerationJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImageProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    EndpointUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    OpenAiAccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    MainlineModelId = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastTestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageProviders_OpenAiAccounts_OpenAiAccountId",
                        column: x => x.OpenAiAccountId,
                        principalTable: "OpenAiAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageProviderModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImageProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", nullable: false),
                    QualitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    OutputFormatsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SupportsGenerate = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsEdit = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsRegionalGuide = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxReferenceImages = table.Column<int>(type: "INTEGER", nullable: false),
                    SupportsTransparentBackground = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsStreaming = table.Column<bool>(type: "INTEGER", nullable: false),
                    RasterConstraintsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageProviderModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageProviderModels_ImageProviders_ImageProviderId",
                        column: x => x.ImageProviderId,
                        principalTable: "ImageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectImageGenerationJobs_ImageProviderId",
                table: "ProjectImageGenerationJobs",
                column: "ImageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageProviderModels_ImageProviderId_ModelId",
                table: "ImageProviderModels",
                columns: new[] { "ImageProviderId", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageProviders_IsDefault",
                table: "ImageProviders",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_ImageProviders_Name",
                table: "ImageProviders",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageProviders_OpenAiAccountId",
                table: "ImageProviders",
                column: "OpenAiAccountId");

            // Existing users generated images through their Codex sign-in. Keep that
            // working by turning the signed-in Codex account into the default image
            // provider with the GPT Image models the route already served. New users
            // configure image providers explicitly.
            migrationBuilder.Sql("""
                INSERT INTO ImageProviders (Name, DisplayName, Kind, OpenAiAccountId, IsDefault, ConfigurationJson, CreatedAt, UpdatedAt)
                SELECT 'chatgpt-account', 'ChatGPT account', 'ChatGptAccount', a.Id, 1, '{}', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM OpenAiAccounts a
                WHERE a.Kind = 'Codex'
                  AND EXISTS (SELECT 1 FROM OAuthTokens t WHERE t.OpenAiAccountId = a.Id AND t.AccessToken <> '')
                ORDER BY a.Id
                LIMIT 1;
                """);
            migrationBuilder.Sql("""
                WITH bundled (ModelId, DisplayName, QualitiesJson, IsDefault, SortOrder) AS (VALUES
                    ('gpt-image-2.5-flare', 'GPT Image 2.5 Flare', '["auto","low","medium","high","xhigh","max"]', 1, 0),
                    ('gpt-image-2.5-sunburst', 'GPT Image 2.5 Sunburst', '["auto","low","medium","high","xhigh","max"]', 0, 1),
                    ('gpt-image-2', 'GPT Image 2', '["auto","low","medium","high"]', 0, 2),
                    ('gpt-image-2.5-flare-2026-09-08', 'GPT Image 2.5 Flare (2026-09-08)', '["auto","low","medium","high","xhigh","max"]', 0, 3),
                    ('gpt-image-2.5-sunburst-2026-09-08', 'GPT Image 2.5 Sunburst (2026-09-08)', '["auto","low","medium","high","xhigh","max"]', 0, 4),
                    ('gpt-image-2-2026-04-21', 'GPT Image 2 (2026-04-21)', '["auto","low","medium","high"]', 0, 5))
                INSERT INTO ImageProviderModels (
                    ImageProviderId, ModelId, DisplayName, Origin, QualitiesJson, OutputFormatsJson,
                    SupportsGenerate, SupportsEdit, SupportsRegionalGuide, MaxReferenceImages,
                    SupportsTransparentBackground, SupportsStreaming, RasterConstraintsJson,
                    IsDefault, SortOrder, CreatedAt, UpdatedAt)
                SELECT p.Id, b.ModelId, b.DisplayName, 'Bundled', b.QualitiesJson, '["png","jpeg","webp"]',
                    1, 1, 1, 16,
                    1, 1, '{"sizeMultiple":16,"minimumPixels":655360,"maximumPixels":8294400,"maximumEdge":3840,"maximumAspectRatio":3,"targetPixels":1572864}',
                    b.IsDefault, b.SortOrder, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM ImageProviders p, bundled b
                WHERE p.Name = 'chatgpt-account';
                """);
            migrationBuilder.Sql("""
                UPDATE ProjectImageGenerationJobs
                SET ImageProviderId = (SELECT Id FROM ImageProviders WHERE Name = 'chatgpt-account');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageProviderModels");

            migrationBuilder.DropTable(
                name: "ImageProviders");

            migrationBuilder.DropIndex(
                name: "IX_ProjectImageGenerationJobs_ImageProviderId",
                table: "ProjectImageGenerationJobs");

            migrationBuilder.DropColumn(
                name: "ImageProviderId",
                table: "ProjectImageGenerationJobs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetainedSourceCoreM4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveExtractionVersionId",
                table: "IngestSources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceExtractionVersionId",
                table: "IngestSourcePages",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "SourceExtractionVersionId",
                table: "IngestSourceChunks",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "IngestSourceBlocks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedText",
                table: "IngestSourceBlocks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceExtractionVersionId",
                table: "IngestSourceBlocks",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "BibliographicRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerTitle = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EditorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IssuedYear = table.Column<int>(type: "INTEGER", nullable: true),
                    Publisher = table.Column<string>(type: "TEXT", nullable: false),
                    PublisherPlace = table.Column<string>(type: "TEXT", nullable: false),
                    Volume = table.Column<string>(type: "TEXT", nullable: false),
                    Issue = table.Column<string>(type: "TEXT", nullable: false),
                    Pages = table.Column<string>(type: "TEXT", nullable: false),
                    Doi = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    AccessedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Isbn = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BibliographicRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BibliographicRecords_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BibliographicRecords_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceExtractionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Extractor = table.Column<string>(type: "TEXT", nullable: false),
                    ExtractorVersion = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Diagnostics = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedText = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceExtractionVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceExtractionVersions_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceOriginalBlobs",
                columns: table => new
                {
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceOriginalBlobs", x => x.Sha256);
                    table.CheckConstraint("CK_SourceOriginalBlobs_Length", "Length >= 0");
                });

            migrationBuilder.CreateTable(
                name: "SourceOriginals",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceOriginals", x => x.SourceId);
                    table.ForeignKey(
                        name: "FK_SourceOriginals_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceLocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExtractionVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceBlockId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    NormalizedStart = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalizedLength = table.Column<int>(type: "INTEGER", nullable: false),
                    Locator = table.Column<string>(type: "TEXT", nullable: false),
                    Quote = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResolutionState = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceLocations", x => x.Id);
                    table.CheckConstraint("CK_SourceLocations_NormalizedRange", "NormalizedStart >= 0 AND NormalizedLength >= 0");
                    table.ForeignKey(
                        name: "FK_SourceLocations_IngestSourceBlocks_SourceBlockId",
                        column: x => x.SourceBlockId,
                        principalTable: "IngestSourceBlocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SourceLocations_IngestSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "IngestSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceLocations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceLocations_SourceExtractionVersions_ExtractionVersionId",
                        column: x => x.ExtractionVersionId,
                        principalTable: "SourceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceOriginalChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    BlobSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ByteLength = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceOriginalChunks", x => x.Id);
                    table.CheckConstraint("CK_SourceOriginalChunks_ByteLength", "ByteLength > 0 AND ByteLength <= 8388608");
                    table.ForeignKey(
                        name: "FK_SourceOriginalChunks_SourceOriginalBlobs_BlobSha256",
                        column: x => x.BlobSha256,
                        principalTable: "SourceOriginalBlobs",
                        principalColumn: "Sha256",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceOriginalChunks_SourceOriginals_SourceId",
                        column: x => x.SourceId,
                        principalTable: "SourceOriginals",
                        principalColumn: "SourceId",
                        onDelete: ReferentialAction.Cascade);
                });

            // This is deliberately an additive preservation migration. Legacy
            // databases retain their extracted text and identifiers, but did not
            // retain upload bytes, so no original blob or fabricated byte hash is
            // created. The source ID is reused as the deterministic extraction ID
            // only for these one-per-source legacy rows.
            migrationBuilder.Sql("""
                INSERT INTO SourceExtractionVersions
                    (Id, SourceId, Ordinal, Extractor, ExtractorVersion, OptionsJson,
                     ContentHash, Status, Diagnostics, NormalizedText, CreatedAt)
                SELECT Id, Id, 0, 'legacy-ingest', 'pre-m4', '{}', SourceHash,
                       'LegacyImmutable', 'Migrated from legacy IngestSource text.',
                       SourceText, CreatedAt
                FROM IngestSources;

                INSERT INTO SourceOriginals
                    (SourceId, State, FileName, MediaType, Length, Sha256, CreatedAt)
                SELECT Id, 'OriginalUnavailable',
                       COALESCE(NULLIF(trim(Title), ''), 'legacy-source'),
                       COALESCE(NULLIF(trim(ContentType), ''), 'application/octet-stream'),
                       0, NULL, CreatedAt
                FROM IngestSources;

                UPDATE IngestSources
                SET ActiveExtractionVersionId = Id;

                UPDATE IngestSourceChunks
                SET SourceExtractionVersionId = SourceId;

                UPDATE IngestSourcePages
                SET SourceExtractionVersionId = SourceId;

                UPDATE IngestSourceBlocks
                SET SourceExtractionVersionId = SourceId,
                    NormalizedText = COALESCE((
                        SELECT substr(
                            source.SourceText,
                            CASE WHEN IngestSourceBlocks.StartChar < 0 THEN 1 ELSE IngestSourceBlocks.StartChar + 1 END,
                            CASE
                                WHEN IngestSourceBlocks.EndChar <= IngestSourceBlocks.StartChar THEN 0
                                ELSE MIN(1048576, IngestSourceBlocks.EndChar - MAX(IngestSourceBlocks.StartChar, 0))
                            END)
                        FROM IngestSources AS source
                        WHERE source.Id = IngestSourceBlocks.SourceId), ''),
                    ContentHash = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_IngestSources_ActiveExtractionVersionId",
                table: "IngestSources",
                column: "ActiveExtractionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourcePages_SourceExtractionVersionId",
                table: "IngestSourcePages",
                column: "SourceExtractionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourceChunks_SourceExtractionVersionId",
                table: "IngestSourceChunks",
                column: "SourceExtractionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestSourceBlocks_SourceExtractionVersionId_Index",
                table: "IngestSourceBlocks",
                columns: new[] { "SourceExtractionVersionId", "Index" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IngestSourceBlocks_NormalizedText",
                table: "IngestSourceBlocks",
                sql: "length(NormalizedText) <= 1048576");

            migrationBuilder.CreateIndex(
                name: "IX_BibliographicRecords_ProjectId_Title",
                table: "BibliographicRecords",
                columns: new[] { "ProjectId", "Title" });

            migrationBuilder.CreateIndex(
                name: "IX_BibliographicRecords_SourceId",
                table: "BibliographicRecords",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceExtractionVersions_SourceId_Ordinal",
                table: "SourceExtractionVersions",
                columns: new[] { "SourceId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceLocations_ExtractionVersionId",
                table: "SourceLocations",
                column: "ExtractionVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceLocations_ProjectId_SourceId_ExtractionVersionId",
                table: "SourceLocations",
                columns: new[] { "ProjectId", "SourceId", "ExtractionVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceLocations_SourceBlockId",
                table: "SourceLocations",
                column: "SourceBlockId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceLocations_SourceId",
                table: "SourceLocations",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceOriginalChunks_BlobSha256",
                table: "SourceOriginalChunks",
                column: "BlobSha256");

            migrationBuilder.CreateIndex(
                name: "IX_SourceOriginalChunks_SourceId_Index",
                table: "SourceOriginalChunks",
                columns: new[] { "SourceId", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceOriginals_Sha256",
                table: "SourceOriginals",
                column: "Sha256");

            migrationBuilder.AddForeignKey(
                name: "FK_IngestSourceBlocks_SourceExtractionVersions_SourceExtractionVersionId",
                table: "IngestSourceBlocks",
                column: "SourceExtractionVersionId",
                principalTable: "SourceExtractionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IngestSourceChunks_SourceExtractionVersions_SourceExtractionVersionId",
                table: "IngestSourceChunks",
                column: "SourceExtractionVersionId",
                principalTable: "SourceExtractionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IngestSourcePages_SourceExtractionVersions_SourceExtractionVersionId",
                table: "IngestSourcePages",
                column: "SourceExtractionVersionId",
                principalTable: "SourceExtractionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropColumn(
                name: "SourceHash",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "SourceText",
                table: "IngestSources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceHash",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceText",
                table: "IngestSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE IngestSources
                SET SourceHash = COALESCE((
                        SELECT extraction.ContentHash
                        FROM SourceExtractionVersions AS extraction
                        WHERE extraction.Id = IngestSources.ActiveExtractionVersionId), ''),
                    SourceText = COALESCE((
                        SELECT extraction.NormalizedText
                        FROM SourceExtractionVersions AS extraction
                        WHERE extraction.Id = IngestSources.ActiveExtractionVersionId), '');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_IngestSourceBlocks_SourceExtractionVersions_SourceExtractionVersionId",
                table: "IngestSourceBlocks");

            migrationBuilder.DropForeignKey(
                name: "FK_IngestSourceChunks_SourceExtractionVersions_SourceExtractionVersionId",
                table: "IngestSourceChunks");

            migrationBuilder.DropForeignKey(
                name: "FK_IngestSourcePages_SourceExtractionVersions_SourceExtractionVersionId",
                table: "IngestSourcePages");

            migrationBuilder.DropTable(
                name: "BibliographicRecords");

            migrationBuilder.DropTable(
                name: "SourceLocations");

            migrationBuilder.DropTable(
                name: "SourceOriginalChunks");

            migrationBuilder.DropTable(
                name: "SourceExtractionVersions");

            migrationBuilder.DropTable(
                name: "SourceOriginalBlobs");

            migrationBuilder.DropTable(
                name: "SourceOriginals");

            migrationBuilder.DropIndex(
                name: "IX_IngestSources_ActiveExtractionVersionId",
                table: "IngestSources");

            migrationBuilder.DropIndex(
                name: "IX_IngestSourcePages_SourceExtractionVersionId",
                table: "IngestSourcePages");

            migrationBuilder.DropIndex(
                name: "IX_IngestSourceChunks_SourceExtractionVersionId",
                table: "IngestSourceChunks");

            migrationBuilder.DropIndex(
                name: "IX_IngestSourceBlocks_SourceExtractionVersionId_Index",
                table: "IngestSourceBlocks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IngestSourceBlocks_NormalizedText",
                table: "IngestSourceBlocks");

            migrationBuilder.DropColumn(
                name: "ActiveExtractionVersionId",
                table: "IngestSources");

            migrationBuilder.DropColumn(
                name: "SourceExtractionVersionId",
                table: "IngestSourcePages");

            migrationBuilder.DropColumn(
                name: "SourceExtractionVersionId",
                table: "IngestSourceChunks");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "IngestSourceBlocks");

            migrationBuilder.DropColumn(
                name: "NormalizedText",
                table: "IngestSourceBlocks");

            migrationBuilder.DropColumn(
                name: "SourceExtractionVersionId",
                table: "IngestSourceBlocks");
        }
    }
}

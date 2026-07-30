using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicationEditionsV10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicationEditionMigrationJournals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MigrationName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    BackupPath = table.Column<string>(type: "TEXT", nullable: false),
                    SourceProfileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSelectionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourcePlacementCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EditionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OutlineItemCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PlacementCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceHash = table.Column<string>(type: "TEXT", nullable: false),
                    TargetHash = table.Column<string>(type: "TEXT", nullable: false),
                    ValidationReportJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditionMigrationJournals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicationEditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", nullable: false),
                    VendorProfileVersion = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    TitleOverride = table.Column<string>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", nullable: false),
                    Copyright = table.Column<string>(type: "TEXT", nullable: false),
                    Isbn = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeVisibleTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberActs = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberChapters = table.Column<bool>(type: "INTEGER", nullable: false),
                    TitlePageMode = table.Column<string>(type: "TEXT", nullable: false),
                    PrintPicturePageSpreadMode = table.Column<string>(type: "TEXT", nullable: false),
                    EpubPicturePageSpreadMode = table.Column<string>(type: "TEXT", nullable: false),
                    Binding = table.Column<string>(type: "TEXT", nullable: false),
                    Paper = table.Column<string>(type: "TEXT", nullable: false),
                    Ink = table.Column<string>(type: "TEXT", nullable: false),
                    Bleed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PageWidthInches = table.Column<double>(type: "REAL", nullable: false),
                    PageHeightInches = table.Column<double>(type: "REAL", nullable: false),
                    PageMarginInches = table.Column<double>(type: "REAL", nullable: false),
                    BodyFontSizePoints = table.Column<double>(type: "REAL", nullable: false),
                    BodyLineHeight = table.Column<double>(type: "REAL", nullable: false),
                    SelectedCoverChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEditions_Chapters_SelectedCoverChapterId",
                        column: x => x.SelectedCoverChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PublicationEditions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationEditionAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeHash = table.Column<string>(type: "TEXT", nullable: false),
                    AfterHash = table.Column<string>(type: "TEXT", nullable: false),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditionAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEditionAuditEntries_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationEditionOutlineItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditionOutlineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEditionOutlineItems_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationEditionOutlineItems_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationEditionOutlineItems_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationEditionStyleMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ManuscriptStyleDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SemanticRole = table.Column<string>(type: "TEXT", nullable: false),
                    OverrideJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEditionStyleMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEditionStyleMappings_ManuscriptStyleDefinitions_ManuscriptStyleDefinitionId",
                        column: x => x.ManuscriptStyleDefinitionId,
                        principalTable: "ManuscriptStyleDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationEditionStyleMappings_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationImagePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PlacementKind = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationImagePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationImagePlacements_Acts_ActId",
                        column: x => x.ActId,
                        principalTable: "Acts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationImagePlacements_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationImagePlacements_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublicationImagePlacements_PublishAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationMatter",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EditionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    ManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationMatter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationMatter_PublicationEditions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "PublicationEditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionAuditEntries_EditionId_CreatedAt",
                table: "PublicationEditionAuditEntries",
                columns: new[] { "EditionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionMigrationJournals_MigrationName_StartedAt",
                table: "PublicationEditionMigrationJournals",
                columns: new[] { "MigrationName", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionOutlineItems_ActId",
                table: "PublicationEditionOutlineItems",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionOutlineItems_ChapterId",
                table: "PublicationEditionOutlineItems",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionOutlineItems_EditionId_SortOrder",
                table: "PublicationEditionOutlineItems",
                columns: new[] { "EditionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionOutlineItems_EditionId_TargetKind_TargetId",
                table: "PublicationEditionOutlineItems",
                columns: new[] { "EditionId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditions_ProjectId_IsDefault",
                table: "PublicationEditions",
                columns: new[] { "ProjectId", "IsDefault" },
                unique: true,
                filter: "\"IsDefault\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditions_ProjectId_Name",
                table: "PublicationEditions",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditions_SelectedCoverChapterId",
                table: "PublicationEditions",
                column: "SelectedCoverChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionStyleMappings_EditionId_ManuscriptStyleDefinitionId",
                table: "PublicationEditionStyleMappings",
                columns: new[] { "EditionId", "ManuscriptStyleDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionStyleMappings_EditionId_SemanticRole",
                table: "PublicationEditionStyleMappings",
                columns: new[] { "EditionId", "SemanticRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEditionStyleMappings_ManuscriptStyleDefinitionId",
                table: "PublicationEditionStyleMappings",
                column: "ManuscriptStyleDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationImagePlacements_ActId",
                table: "PublicationImagePlacements",
                column: "ActId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationImagePlacements_AssetId",
                table: "PublicationImagePlacements",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationImagePlacements_ChapterId",
                table: "PublicationImagePlacements",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicationImagePlacements_EditionId_TargetKind_TargetId_PlacementKind_SortOrder",
                table: "PublicationImagePlacements",
                columns: new[] { "EditionId", "TargetKind", "TargetId", "PlacementKind", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationMatter_EditionId_Location_SortOrder",
                table: "PublicationMatter",
                columns: new[] { "EditionId", "Location", "SortOrder" });

            migrationBuilder.Sql(
                """
                INSERT INTO PublicationEditions (
                    Id, ProjectId, Name, Format, Vendor, VendorProfileVersion, Status, IsDefault, Revision,
                    TitleOverride, Subtitle, Author, Language, Publisher, Copyright, Isbn, Description,
                    IncludeTableOfContents, IncludeVisibleTableOfContents, IncludeActSynopses,
                    IncludeChapterSynopses, IncludeActHeadings, IncludeChapterHeadings, NumberActs,
                    NumberChapters, TitlePageMode, PrintPicturePageSpreadMode, EpubPicturePageSpreadMode,
                    Binding, Paper, Ink, Bleed, PageWidthInches, PageHeightInches, PageMarginInches,
                    BodyFontSizePoints, BodyLineHeight, SelectedCoverChapterId, CreatedAt, UpdatedAt)
                SELECT
                    Id, ProjectId, 'Default paperback', 'Paperback', 'Generic', 'legacy-v9', 'Draft', 1, 0,
                    TitleOverride, Subtitle, Author, Language, Publisher, Copyright, Isbn, Description,
                    IncludeTableOfContents, IncludeVisibleTableOfContents, IncludeActSynopses,
                    IncludeChapterSynopses, IncludeActHeadings, IncludeChapterHeadings, NumberActs,
                    NumberChapters, TitlePageMode, PrintPicturePageSpreadMode, EpubPicturePageSpreadMode,
                    'PerfectBound', 'White', 'BlackAndWhite', 0, PageWidthInches, PageHeightInches,
                    PageMarginInches, BodyFontSizePoints, BodyLineHeight, SelectedCoverChapterId,
                    CreatedAt, UpdatedAt
                FROM PublishProfiles;

                INSERT INTO PublicationEditions (
                    Id, ProjectId, Name, Format, Vendor, VendorProfileVersion, Status, IsDefault, Revision,
                    TitleOverride, Subtitle, Author, Language, Publisher, Copyright, Isbn, Description,
                    IncludeTableOfContents, IncludeVisibleTableOfContents, IncludeActSynopses,
                    IncludeChapterSynopses, IncludeActHeadings, IncludeChapterHeadings, NumberActs,
                    NumberChapters, TitlePageMode, PrintPicturePageSpreadMode, EpubPicturePageSpreadMode,
                    Binding, Paper, Ink, Bleed, PageWidthInches, PageHeightInches, PageMarginInches,
                    BodyFontSizePoints, BodyLineHeight, SelectedCoverChapterId, CreatedAt, UpdatedAt)
                SELECT
                    p.Id, p.Id, 'Default paperback', 'Paperback', 'Generic', 'legacy-v9', 'Draft', 1, 0,
                    p.Name, '', '', 'en', '', '', '', '',
                    1, 1, 0, 0, 1, 1, 0, 0, 'Automatic', 'WholeSpread', 'RequestLandscape',
                    'PerfectBound', 'White', 'BlackAndWhite', 0, 8.5, 11.0, 0.75, 12.0, 1.55,
                    NULL, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM Projects p
                WHERE NOT EXISTS (SELECT 1 FROM PublishProfiles profile WHERE profile.ProjectId = p.Id)
                  AND (
                    EXISTS (SELECT 1 FROM PublishOutlineSelections item WHERE item.ProjectId = p.Id)
                    OR EXISTS (SELECT 1 FROM PublishImagePlacements placement WHERE placement.ProjectId = p.Id)
                  );

                INSERT INTO PublicationEditionOutlineItems (
                    Id, EditionId, TargetKind, TargetId, ActId, ChapterId, IsIncluded, SortOrder,
                    CreatedAt, UpdatedAt)
                SELECT
                    item.Id, COALESCE(profile.Id, item.ProjectId), item.TargetKind, item.TargetId,
                    CASE WHEN item.TargetKind = 'Act' THEN item.TargetId ELSE NULL END,
                    CASE WHEN item.TargetKind = 'Chapter' THEN item.TargetId ELSE NULL END,
                    item.IsIncluded,
                    ROW_NUMBER() OVER (PARTITION BY item.ProjectId ORDER BY item.CreatedAt, item.Id) - 1,
                    item.CreatedAt, item.UpdatedAt
                FROM PublishOutlineSelections item
                LEFT JOIN PublishProfiles profile ON profile.ProjectId = item.ProjectId;

                INSERT INTO PublicationImagePlacements (
                    Id, EditionId, AssetId, TargetKind, TargetId, ActId, ChapterId, PlacementKind,
                    SortOrder, Caption, CreatedAt, UpdatedAt)
                SELECT
                    placement.Id, COALESCE(profile.Id, placement.ProjectId), placement.AssetId,
                    placement.TargetKind, placement.TargetId,
                    CASE WHEN placement.TargetKind = 'Act' THEN placement.TargetId ELSE NULL END,
                    CASE WHEN placement.TargetKind = 'Chapter' THEN placement.TargetId ELSE NULL END,
                    placement.PlacementKind, placement.SortOrder, placement.Caption,
                    placement.CreatedAt, placement.UpdatedAt
                FROM PublishImagePlacements placement
                LEFT JOIN PublishProfiles profile ON profile.ProjectId = placement.ProjectId;

                CREATE TEMP TABLE __PublicationMatterV10 (
                    Id TEXT NOT NULL,
                    EditionId TEXT NOT NULL,
                    Location TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    Body TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL
                );
                INSERT INTO __PublicationMatterV10
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                       lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                       lower(hex(randomblob(6))), Id, 'Front', 'Dedication', 'Dedication', Dedication, 0
                FROM PublishProfiles WHERE trim(Dedication) <> '';
                INSERT INTO __PublicationMatterV10
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                       lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                       lower(hex(randomblob(6))), Id, 'Back', 'Acknowledgments', 'Acknowledgments', Acknowledgments, 0
                FROM PublishProfiles WHERE trim(Acknowledgments) <> '';
                INSERT INTO __PublicationMatterV10
                SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                       lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                       lower(hex(randomblob(6))), Id, 'Back', 'References', 'References', "References", 1
                FROM PublishProfiles WHERE trim("References") <> '';
                INSERT INTO PublicationMatter (
                    Id, EditionId, Location, Kind, Title, ManuscriptJson, Revision, IsIncluded,
                    SortOrder, CreatedAt, UpdatedAt)
                SELECT
                    Id, EditionId, Location, Kind, Title,
                    json_object(
                        'schemaVersion', 2,
                        'manuscriptId', Id,
                        'revision', 1,
                        'content', json_array(json_object(
                            'id', 'matter-' || replace(Id, '-', ''),
                            'type', 'paragraph',
                            'styleRole', 'body',
                            'headingLevel', NULL,
                            'imageId', NULL,
                            'altText', NULL,
                            'content', json_array(json_object(
                                'type', 'text',
                                'text', Body,
                                'marks', json_array()))))
                    ),
                    1, 1, SortOrder, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM __PublicationMatterV10;
                DROP TABLE __PublicationMatterV10;
                """);

            migrationBuilder.DropTable(name: "PublishImagePlacements");
            migrationBuilder.DropTable(name: "PublishOutlineSelections");
            migrationBuilder.DropTable(name: "PublishProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationEditionAuditEntries");

            migrationBuilder.DropTable(
                name: "PublicationEditionMigrationJournals");

            migrationBuilder.DropTable(
                name: "PublicationEditionOutlineItems");

            migrationBuilder.DropTable(
                name: "PublicationEditionStyleMappings");

            migrationBuilder.DropTable(
                name: "PublicationImagePlacements");

            migrationBuilder.DropTable(
                name: "PublicationMatter");

            migrationBuilder.DropTable(
                name: "PublicationEditions");

            migrationBuilder.CreateTable(
                name: "PublishImagePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlacementKind = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishImagePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishImagePlacements_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PublishImagePlacements_PublishAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "PublishAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishOutlineSelections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishOutlineSelections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishOutlineSelections_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublishProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelectedCoverChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Acknowledgments = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    BodyFontSizePoints = table.Column<double>(type: "REAL", nullable: false),
                    BodyLineHeight = table.Column<double>(type: "REAL", nullable: false),
                    Copyright = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Dedication = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    EpubPicturePageSpreadMode = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeActHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeActSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterHeadings = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeChapterSynopses = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeVisibleTableOfContents = table.Column<bool>(type: "INTEGER", nullable: false),
                    Isbn = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    NumberActs = table.Column<bool>(type: "INTEGER", nullable: false),
                    NumberChapters = table.Column<bool>(type: "INTEGER", nullable: false),
                    PageHeightInches = table.Column<double>(type: "REAL", nullable: false),
                    PageMarginInches = table.Column<double>(type: "REAL", nullable: false),
                    PageWidthInches = table.Column<double>(type: "REAL", nullable: false),
                    PrintPicturePageSpreadMode = table.Column<string>(type: "TEXT", nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", nullable: false),
                    References = table.Column<string>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", nullable: false),
                    TitleOverride = table.Column<string>(type: "TEXT", nullable: false),
                    TitlePageMode = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublishProfiles_Chapters_SelectedCoverChapterId",
                        column: x => x.SelectedCoverChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PublishProfiles_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublishImagePlacements_AssetId",
                table: "PublishImagePlacements",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_PublishImagePlacements_ProjectId_TargetKind_TargetId_PlacementKind_SortOrder",
                table: "PublishImagePlacements",
                columns: new[] { "ProjectId", "TargetKind", "TargetId", "PlacementKind", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PublishOutlineSelections_ProjectId_TargetKind_TargetId",
                table: "PublishOutlineSelections",
                columns: new[] { "ProjectId", "TargetKind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishProfiles_ProjectId",
                table: "PublishProfiles",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublishProfiles_SelectedCoverChapterId",
                table: "PublishProfiles",
                column: "SelectedCoverChapterId");
        }
    }
}

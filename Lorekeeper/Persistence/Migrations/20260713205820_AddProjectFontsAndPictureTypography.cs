using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectFontsAndPictureTypography : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectFontFamilies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFontFamilies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectFontFamilies_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectFontFaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FamilyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubfamilyName = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false),
                    Italic = table.Column<bool>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFontFaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectFontFaces_ProjectFontFamilies_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "ProjectFontFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFontFaces_FamilyId_Weight_Italic",
                table: "ProjectFontFaces",
                columns: new[] { "FamilyId", "Weight", "Italic" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectFontFamilies_ProjectId_Name",
                table: "ProjectFontFamilies",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.Sql(
                """
                UPDATE Chapters
                SET PageLayoutJson = json_set(
                    PageLayoutJson,
                    '$.textElements',
                    json((
                        SELECT json_group_array(json(
                            json_remove(
                                json_set(
                                    value,
                                    '$.fontFamilyKey', CASE json_extract(value, '$.fontFamily')
                                        WHEN 'Serif' THEN 'builtin:lora'
                                        WHEN 'Sans' THEN 'builtin:nunito'
                                        WHEN 'Display' THEN 'builtin:fredoka'
                                        WHEN 'Monospace' THEN 'builtin:roboto-mono'
                                        ELSE 'builtin:andika'
                                    END,
                                    '$.fontWeight', 400,
                                    '$.italic', json('false'),
                                    '$.fontSizePoints', ROUND(
                                        COALESCE(CAST(json_extract(value, '$.fontSizePercent') AS REAL), 4.5)
                                        * CASE Chapters.PageLayoutKind
                                            WHEN 'SingleLandscape' THEN 11.0
                                            WHEN 'DoubleLandscape' THEN 11.0
                                            ELSE 8.5
                                          END
                                        * 0.72,
                                        3),
                                    '$.letterSpacingEm', 0
                                ),
                                '$.fontFamily',
                                '$.fontSizePercent'
                            )
                        ))
                        FROM json_each(Chapters.PageLayoutJson, '$.textElements')
                    ))
                )
                WHERE VisualMode = 'PicturePage'
                  AND json_valid(PageLayoutJson)
                  AND json_type(PageLayoutJson, '$.textElements') = 'array';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE Chapters
                SET PageLayoutJson = json_set(
                    PageLayoutJson,
                    '$.textElements',
                    json((
                        SELECT json_group_array(json(
                            json_remove(
                                json_set(
                                    value,
                                    '$.fontFamily', CASE json_extract(value, '$.fontFamilyKey')
                                        WHEN 'builtin:lora' THEN 'Serif'
                                        WHEN 'builtin:nunito' THEN 'Sans'
                                        WHEN 'builtin:fredoka' THEN 'Display'
                                        WHEN 'builtin:roboto-mono' THEN 'Monospace'
                                        ELSE 'Sans'
                                    END,
                                    '$.fontSizePercent', ROUND(
                                        COALESCE(CAST(json_extract(value, '$.fontSizePoints') AS REAL), 24.0)
                                        / (CASE Chapters.PageLayoutKind
                                            WHEN 'SingleLandscape' THEN 11.0
                                            WHEN 'DoubleLandscape' THEN 11.0
                                            ELSE 8.5
                                           END * 0.72),
                                        3)
                                ),
                                '$.fontFamilyKey',
                                '$.fontWeight',
                                '$.italic',
                                '$.fontSizePoints',
                                '$.letterSpacingEm'
                            )
                        ))
                        FROM json_each(Chapters.PageLayoutJson, '$.textElements')
                    ))
                )
                WHERE VisualMode = 'PicturePage'
                  AND json_valid(PageLayoutJson)
                  AND json_type(PageLayoutJson, '$.textElements') = 'array';
                """);

            migrationBuilder.DropTable(
                name: "ProjectFontFaces");

            migrationBuilder.DropTable(
                name: "ProjectFontFamilies");
        }
    }
}

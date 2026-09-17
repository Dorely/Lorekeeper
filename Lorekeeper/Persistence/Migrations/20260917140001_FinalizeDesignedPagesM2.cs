using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260917140001_FinalizeDesignedPagesM2")]
public sealed class FinalizeDesignedPagesM2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PageCompositionVariants");
        migrationBuilder.DropTable(name: "PageCompositions");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PageCompositions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                ChapterId = table.Column<Guid>(type: "TEXT", nullable: true),
                PublicationSectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceCompositionId = table.Column<Guid>(type: "TEXT", nullable: true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                SemanticManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                ActiveAuthoringVariantId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DetachedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PageCompositions", x => x.Id);
                table.ForeignKey("FK_PageCompositions_Projects_ProjectId", x => x.ProjectId,
                    "Projects", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateTable(
            name: "PageCompositionVariants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompositionId = table.Column<Guid>(type: "TEXT", nullable: false),
                GeometryKey = table.Column<string>(type: "TEXT", nullable: false),
                SceneJson = table.Column<string>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DetachedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PageCompositionVariants", x => x.Id);
                table.ForeignKey("FK_PageCompositionVariants_PageCompositions_CompositionId", x => x.CompositionId,
                    "PageCompositions", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.Sql("""
            INSERT INTO "PageCompositions" (
                "Id", "ProjectId", "ChapterId", "PublicationSectionId", "EditionId", "SourceCompositionId",
                "Name", "SemanticManuscriptJson", "Revision", "ActiveAuthoringVariantId", "CreatedAt", "UpdatedAt", "DetachedAt")
            SELECT content."Id", content."ProjectId", NULL, NULL, content."EditionId",
                CASE WHEN content."DesignedPageId" <> content."Id" THEN content."DesignedPageId" ELSE NULL END,
                page."Name", content."SemanticManuscriptJson", content."Revision", content."ActiveVariantId",
                content."CreatedAt", content."UpdatedAt", NULL
            FROM "DesignedPageContents" AS content
            INNER JOIN "DesignedPages" AS page ON page."Id" = content."DesignedPageId";
            INSERT INTO "PageCompositionVariants" (
                "Id", "CompositionId", "GeometryKey", "SceneJson", "Revision", "CreatedAt", "UpdatedAt", "DetachedAt")
            SELECT "Id", "ContentId", "GeometryKey", "SceneJson", "Revision", "CreatedAt", "UpdatedAt", NULL
            FROM "DesignedPageVariants";
            """);
        migrationBuilder.CreateIndex("IX_PageCompositions_ProjectId", "PageCompositions", "ProjectId");
        migrationBuilder.CreateIndex("IX_PageCompositionVariants_CompositionId_GeometryKey", "PageCompositionVariants",
            new[] { "CompositionId", "GeometryKey" }, unique: true);
    }
}

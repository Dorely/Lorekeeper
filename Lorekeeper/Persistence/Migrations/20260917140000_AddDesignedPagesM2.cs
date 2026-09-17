using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260917140000_AddDesignedPagesM2")]
public sealed class AddDesignedPagesM2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DesignedPages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                ScopeEditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DesignedPages", x => x.Id);
                table.ForeignKey("FK_DesignedPages_Projects_ProjectId", x => x.ProjectId,
                    "Projects", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_DesignedPages_PublicationEditions_ScopeEditionId", x => x.ScopeEditionId,
                    "PublicationEditions", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DesignedPageContents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                DesignedPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                SemanticManuscriptJson = table.Column<string>(type: "TEXT", nullable: false),
                AccessibilityDescription = table.Column<string>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                ActiveVariantId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DesignedPageContents", x => x.Id);
                table.ForeignKey("FK_DesignedPageContents_DesignedPages_DesignedPageId", x => x.DesignedPageId,
                    "DesignedPages", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_DesignedPageContents_Projects_ProjectId", x => x.ProjectId,
                    "Projects", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_DesignedPageContents_PublicationEditions_EditionId", x => x.EditionId,
                    "PublicationEditions", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DesignedPagePlacementReferences",
            columns: table => new
            {
                ReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                DesignedPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                ContainerKind = table.Column<string>(type: "TEXT", nullable: false),
                ContainerId = table.Column<Guid>(type: "TEXT", nullable: false),
                EditionId = table.Column<Guid>(type: "TEXT", nullable: true),
                ManuscriptRevision = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DesignedPagePlacementReferences", x => x.ReferenceId);
                table.ForeignKey("FK_DesignedPagePlacementReferences_DesignedPages_DesignedPageId", x => x.DesignedPageId,
                    "DesignedPages", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_DesignedPagePlacementReferences_Projects_ProjectId", x => x.ProjectId,
                    "Projects", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_DesignedPagePlacementReferences_PublicationEditions_EditionId", x => x.EditionId,
                    "PublicationEditions", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DesignedPageVariants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ContentId = table.Column<Guid>(type: "TEXT", nullable: false),
                GeometryKey = table.Column<string>(type: "TEXT", nullable: false),
                SceneJson = table.Column<string>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DesignedPageVariants", x => x.Id);
                table.ForeignKey("FK_DesignedPageVariants_DesignedPageContents_ContentId", x => x.ContentId,
                    "DesignedPageContents", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_DesignedPages_ProjectId_ScopeEditionId_UpdatedAt", "DesignedPages",
            new[] { "ProjectId", "ScopeEditionId", "UpdatedAt" });
        migrationBuilder.CreateIndex("IX_DesignedPages_ScopeEditionId", "DesignedPages", "ScopeEditionId");
        migrationBuilder.CreateIndex("IX_DesignedPageContents_ActiveVariantId", "DesignedPageContents", "ActiveVariantId");
        migrationBuilder.CreateIndex("IX_DesignedPageContents_ProjectId_EditionId_UpdatedAt", "DesignedPageContents",
            new[] { "ProjectId", "EditionId", "UpdatedAt" });
        migrationBuilder.CreateIndex("IX_DesignedPageContents_EditionId", "DesignedPageContents", "EditionId");
        migrationBuilder.CreateIndex("IX_DesignedPageContents_DesignedPageId", "DesignedPageContents", "DesignedPageId",
            unique: true, filter: "\"EditionId\" IS NULL");
        migrationBuilder.CreateIndex("IX_DesignedPageContents_DesignedPageId_EditionId", "DesignedPageContents",
            new[] { "DesignedPageId", "EditionId" }, unique: true, filter: "\"EditionId\" IS NOT NULL");
        migrationBuilder.CreateIndex("IX_DesignedPageVariants_ContentId_GeometryKey", "DesignedPageVariants",
            new[] { "ContentId", "GeometryKey" }, unique: true);
        migrationBuilder.CreateIndex("IX_DesignedPagePlacementReferences_DesignedPageId", "DesignedPagePlacementReferences", "DesignedPageId");
        migrationBuilder.CreateIndex("IX_DesignedPagePlacementReferences_EditionId", "DesignedPagePlacementReferences", "EditionId");
        migrationBuilder.CreateIndex("IX_DesignedPagePlacementReferences_ProjectId_DesignedPageId", "DesignedPagePlacementReferences",
            new[] { "ProjectId", "DesignedPageId" });
        migrationBuilder.CreateIndex("IX_DesignedPagePlacementReferences_ProjectId_ContainerKind_ContainerId_EditionId",
            "DesignedPagePlacementReferences", new[] { "ProjectId", "ContainerKind", "ContainerId", "EditionId" });
        migrationBuilder.CreateIndex("IX_DesignedPagePlacementReferences_CorePlacement",
            "DesignedPagePlacementReferences", new[] { "ProjectId", "ContainerKind", "ContainerId", "Id" },
            unique: true, filter: "\"EditionId\" IS NULL");
        migrationBuilder.CreateIndex("IX_DesignedPagePlacementReferences_EditionPlacement",
            "DesignedPagePlacementReferences", new[] { "ProjectId", "ContainerKind", "ContainerId", "EditionId", "Id" },
            unique: true, filter: "\"EditionId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DesignedPagePlacementReferences");
        migrationBuilder.DropTable("DesignedPageVariants");
        migrationBuilder.DropTable("DesignedPageContents");
        migrationBuilder.DropTable("DesignedPages");
    }
}

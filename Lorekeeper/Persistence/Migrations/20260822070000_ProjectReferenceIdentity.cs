using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

/// <summary>
/// Replaces the original local-only project reference key with stable
/// repository/project identity and a nullable local resolution.
/// </summary>
[DbContext(typeof(Lorekeeper.Persistence.AppDbContext))]
[Migration("20260822070000_ProjectReferenceIdentity")]
public partial class ProjectReferenceIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The foundation migration creates identities for existing projects,
        // but keeping this backfill here makes the reference cutover safe for
        // databases that were created while the foundation was being rolled
        // out or repaired.
        migrationBuilder.Sql("""
            INSERT INTO "ProjectVersionRepositories" (
                "Id", "ProjectId", "CreativeRevision", "CreatedAt", "UpdatedAt")
            SELECT
                lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' ||
                    hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' ||
                    hex(randomblob(6))),
                project."Id",
                0,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            FROM "Projects" AS project
            WHERE NOT EXISTS (
                SELECT 1
                FROM "ProjectVersionRepositories" AS repository
                WHERE repository."ProjectId" = project."Id");
            """);

        migrationBuilder.CreateTable(
            name: "ProjectReferences_New",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ReferencingProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                ReferencedRepositoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                ReferencedProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                ResolvedProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                ReferencedProjectName = table.Column<string>(type: "TEXT", nullable: false),
                ReferencedProjectSlug = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectReferences_New", x => x.Id);
                table.CheckConstraint(
                    "CK_ProjectReferences_ResolvedNotSelf",
                    "\"ResolvedProjectId\" IS NULL OR \"ReferencingProjectId\" <> \"ResolvedProjectId\"");
                table.ForeignKey(
                    name: "FK_ProjectReferences_New_Projects_ReferencingProjectId",
                    column: x => x.ReferencingProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ProjectReferences_New_Projects_ResolvedProjectId",
                    column: x => x.ResolvedProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.Sql("""
            INSERT INTO "ProjectReferences_New" (
                "Id",
                "ReferencingProjectId",
                "ReferencedRepositoryId",
                "ReferencedProjectId",
                "ResolvedProjectId",
                "ReferencedProjectName",
                "ReferencedProjectSlug",
                "CreatedAt",
                "ResolvedAt")
            SELECT
                lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' ||
                    hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' ||
                    hex(randomblob(6))),
                reference."ReferencingProjectId",
                repository."Id",
                reference."ReferencedProjectId",
                target."Id",
                target."Name",
                target."Slug",
                reference."CreatedAt",
                reference."CreatedAt"
            FROM "ProjectReferences" AS reference
            INNER JOIN "Projects" AS target
                ON target."Id" = reference."ReferencedProjectId"
            INNER JOIN "ProjectVersionRepositories" AS repository
                ON repository."ProjectId" = target."Id";
            """);

        migrationBuilder.DropTable(name: "ProjectReferences");
        migrationBuilder.RenameTable(
            name: "ProjectReferences_New",
            newName: "ProjectReferences");

        migrationBuilder.CreateIndex(
            name: "IX_ProjectReferences_ReferencingProjectId_ReferencedRepositoryId_ReferencedProjectId",
            table: "ProjectReferences",
            columns: new[] { "ReferencingProjectId", "ReferencedRepositoryId", "ReferencedProjectId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ProjectReferences_ResolvedProjectId",
            table: "ProjectReferences",
            column: "ResolvedProjectId");

        migrationBuilder.Sql("""
            CREATE TRIGGER "TR_ProjectReferences_NoSelfIdentity_Insert"
            BEFORE INSERT ON "ProjectReferences"
            WHEN NEW."ReferencedProjectId" = NEW."ReferencingProjectId"
                AND EXISTS (
                    SELECT 1
                    FROM "ProjectVersionRepositories" AS repository
                    WHERE repository."ProjectId" = NEW."ReferencingProjectId"
                        AND repository."Id" = NEW."ReferencedRepositoryId")
            BEGIN
                SELECT RAISE(ABORT, 'A project cannot reference itself by repository and project identity.');
            END;
            CREATE TRIGGER "TR_ProjectReferences_NoSelfIdentity_Update"
            BEFORE UPDATE OF "ReferencingProjectId", "ReferencedRepositoryId", "ReferencedProjectId"
                ON "ProjectReferences"
            WHEN NEW."ReferencedProjectId" = NEW."ReferencingProjectId"
                AND EXISTS (
                    SELECT 1
                    FROM "ProjectVersionRepositories" AS repository
                    WHERE repository."ProjectId" = NEW."ReferencingProjectId"
                        AND repository."Id" = NEW."ReferencedRepositoryId")
            BEGIN
                SELECT RAISE(ABORT, 'A project cannot reference itself by repository and project identity.');
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS "TR_ProjectReferences_NoSelfIdentity_Insert";
            DROP TRIGGER IF EXISTS "TR_ProjectReferences_NoSelfIdentity_Update";
            """);

        migrationBuilder.CreateTable(
            name: "ProjectReferences_Old",
            columns: table => new
            {
                ReferencingProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                ReferencedProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectReferences_Old", x => new { x.ReferencingProjectId, x.ReferencedProjectId });
                table.CheckConstraint(
                    "CK_ProjectReferences_NotSelf",
                    "\"ReferencingProjectId\" <> \"ReferencedProjectId\"");
                table.ForeignKey(
                    name: "FK_ProjectReferences_Old_Projects_ReferencedProjectId",
                    column: x => x.ReferencedProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ProjectReferences_Old_Projects_ReferencingProjectId",
                    column: x => x.ReferencingProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        // An unresolved cross-machine identity has no representation in the
        // original schema and is intentionally omitted during downgrade.
        migrationBuilder.Sql("""
            INSERT INTO "ProjectReferences_Old" (
                "ReferencingProjectId", "ReferencedProjectId", "CreatedAt")
            SELECT
                reference."ReferencingProjectId",
                reference."ResolvedProjectId",
                reference."CreatedAt"
            FROM "ProjectReferences" AS reference
            INNER JOIN "Projects" AS target
                ON target."Id" = reference."ResolvedProjectId"
            WHERE reference."ResolvedProjectId" IS NOT NULL;
            """);

        migrationBuilder.DropTable(name: "ProjectReferences");
        migrationBuilder.RenameTable(
            name: "ProjectReferences_Old",
            newName: "ProjectReferences");
        migrationBuilder.CreateIndex(
            name: "IX_ProjectReferences_ReferencedProjectId",
            table: "ProjectReferences",
            column: "ReferencedProjectId");
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveProjectMetadataToProjectFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "GraphEntityTypes" (
                    "ProjectId", "Type", "SingularLabel", "PluralLabel", "Color", "Icon",
                    "IsStructural", "IsChapterScoped", "SortOrder", "DefaultProperties",
                    "CreatedAt", "UpdatedAt")
                SELECT
                    p."Id", 'ProjectFact', 'Project fact', 'Project facts', NULL, NULL,
                    1, 0, -150, '{"key":"","value":""}', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM "Projects" AS p
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "GraphEntityTypes" AS existing
                    WHERE existing."ProjectId" = p."Id" AND existing."Type" = 'ProjectFact'
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "GraphNodes" (
                    "ProjectId", "NodeType", "Key", "Label", "Properties", "CreatedAt", "UpdatedAt")
                SELECT
                    p."Id",
                    'Project',
                    lower(replace(p."Id", '-', '')),
                    p."Name",
                    json_object(
                        'slug', p."Slug",
                        'sourceType', 'project',
                        'sourceId', lower(replace(p."Id", '-', '')),
                        'structural', 1),
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Projects" AS p
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "GraphNodes" AS existing
                    WHERE existing."ProjectId" = p."Id"
                        AND existing."NodeType" = 'Project'
                        AND existing."Key" = lower(replace(p."Id", '-', ''))
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "GraphNodes" (
                    "ProjectId", "NodeType", "Key", "Label", "Properties", "CreatedAt", "UpdatedAt")
                SELECT
                    p."Id",
                    'ProjectFact',
                    lower(hex(randomblob(16))),
                    metadata.key,
                    json_object(
                        'key', metadata.key,
                        'value', CASE
                            WHEN metadata.type IN ('array', 'object') THEN metadata.value
                            ELSE COALESCE(CAST(metadata.atom AS TEXT), '')
                        END),
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "Projects" AS p,
                    json_each(COALESCE(NULLIF(p."Metadata", ''), '{}')) AS metadata
                WHERE metadata.key IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM "GraphNodes" AS existing
                        WHERE existing."ProjectId" = p."Id"
                            AND existing."NodeType" = 'ProjectFact'
                            AND lower(json_extract(existing."Properties", '$.key')) = lower(metadata.key)
                    );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "GraphEdges" (
                    "FromNodeId", "ToNodeId", "EdgeType", "Properties", "SortOrder", "CreatedAt", "UpdatedAt")
                SELECT
                    projectNode."Id",
                    factNode."Id",
                    'HasChild',
                    '{}',
                    ROW_NUMBER() OVER (
                        PARTITION BY factNode."ProjectId"
                        ORDER BY json_extract(factNode."Properties", '$.key'), factNode."Id") - 1,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "GraphNodes" AS factNode
                INNER JOIN "GraphNodes" AS projectNode
                    ON projectNode."ProjectId" = factNode."ProjectId"
                    AND projectNode."NodeType" = 'Project'
                    AND projectNode."Key" = lower(replace(factNode."ProjectId", '-', ''))
                WHERE factNode."NodeType" = 'ProjectFact'
                    AND NOT EXISTS (
                        SELECT 1
                        FROM "GraphEdges" AS existing
                        WHERE existing."FromNodeId" = projectNode."Id"
                            AND existing."ToNodeId" = factNode."Id"
                            AND existing."EdgeType" = 'HasChild'
                    );
                """);

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "Projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql("""
                UPDATE "Projects"
                SET "Metadata" = COALESCE((
                    SELECT json_group_object(
                        COALESCE(json_extract(factNode."Properties", '$.key'), factNode."Label", factNode."Key"),
                        COALESCE(json_extract(factNode."Properties", '$.value'), ''))
                    FROM "GraphNodes" AS factNode
                    WHERE factNode."ProjectId" = "Projects"."Id"
                        AND factNode."NodeType" = 'ProjectFact'
                ), '{}');
                """);

            migrationBuilder.Sql("DELETE FROM \"GraphNodes\" WHERE \"NodeType\" = 'ProjectFact';");
            migrationBuilder.Sql("DELETE FROM \"GraphEntityTypes\" WHERE \"Type\" = 'ProjectFact';");
        }
    }
}

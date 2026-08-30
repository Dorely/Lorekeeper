using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactOnlyPrintSettingsV32 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrintFinish",
                table: "PublicationEditions");

            migrationBuilder.RenameColumn(
                name: "PrintRegistryVersion",
                table: "PublicationEditions",
                newName: "PrintArtifactRegistryVersion");

            migrationBuilder.RenameColumn(
                name: "PrintProductKey",
                table: "PublicationEditions",
                newName: "PrintArtifactProfileKey");

            migrationBuilder.Sql(
                """
                UPDATE PublicationEditions
                SET PrintArtifactProfileKey = CASE PrintArtifactProfileKey
                        WHEN 'kdp-pb-bw-white' THEN 'kdp-pb-bw-50-2252'
                        WHEN 'kdp-pb-bw-cream' THEN 'kdp-pb-bw-50-2500'
                        WHEN 'kdp-pb-bw-groundwood' THEN 'kdp-pb-bw-45-2350'
                        WHEN 'kdp-hc-bw-white' THEN 'kdp-hc-bw-50-2252'
                        WHEN 'kdp-hc-bw-cream' THEN 'kdp-hc-bw-50-2500'
                        WHEN 'ingram-pb-bw-white50' THEN 'ingram-pb-bw-50-2009'
                        WHEN 'ingram-pb-bw-cream50' THEN 'ingram-pb-bw-50-2225'
                        WHEN 'ingram-pb-bw-groundwood38' THEN 'ingram-pb-bw-38-2550'
                        WHEN 'ingram-hc-case-bw-white50' THEN 'ingram-hc-case-bw-50-2009'
                        WHEN 'ingram-hc-case-bw-cream50' THEN 'ingram-hc-case-bw-50-2224'
                        WHEN 'ingram-hc-cloth-blue' THEN 'ingram-hc-cloth-bw-50-2009'
                        WHEN 'ingram-hc-cloth-gray' THEN 'ingram-hc-cloth-bw-50-2009'
                        WHEN 'ingram-hc-cloth-blue-jacket' THEN 'ingram-hc-cloth-jacket-bw-50-2009'
                        WHEN 'ingram-hc-cloth-gray-jacket' THEN 'ingram-hc-cloth-jacket-bw-50-2009'
                        WHEN 'bn-pb-bw-cream50-6x9' THEN 'bn-pb-bw-50-6x9'
                        WHEN 'bn-hc-case-bw-cream50-6x9' THEN 'bn-hc-case-bw-50-6x9'
                        WHEN 'bn-hc-jacket-bw-cream50-6x9' THEN 'bn-hc-jacket-bw-50-6x9'
                        ELSE PrintArtifactProfileKey
                    END,
                    PrintArtifactRegistryVersion = CASE
                        WHEN Format IN ('Paperback', 'Hardcover') THEN '2026.08.3'
                        ELSE PrintArtifactRegistryVersion
                    END,
                    PrintTemplateEvidenceJson = replace(
                        replace(
                            replace(
                                replace(PrintTemplateEvidenceJson,
                                    '"productKey":', '"artifactProfileKey":'),
                                'bn-pb-bw-cream50-6x9', 'bn-pb-bw-50-6x9'),
                            'bn-hc-case-bw-cream50-6x9', 'bn-hc-case-bw-50-6x9'),
                        'bn-hc-jacket-bw-cream50-6x9', 'bn-hc-jacket-bw-50-6x9');

                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'kdp-pb-bw-white', 'kdp-pb-bw-50-2252');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'kdp-pb-bw-cream', 'kdp-pb-bw-50-2500');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'kdp-pb-bw-groundwood', 'kdp-pb-bw-45-2350');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'kdp-hc-bw-white', 'kdp-hc-bw-50-2252');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'kdp-hc-bw-cream', 'kdp-hc-bw-50-2500');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-pb-bw-white50', 'ingram-pb-bw-50-2009');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-pb-bw-cream50', 'ingram-pb-bw-50-2225');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-pb-bw-groundwood38', 'ingram-pb-bw-38-2550');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-hc-case-bw-white50', 'ingram-hc-case-bw-50-2009');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-hc-case-bw-cream50', 'ingram-hc-case-bw-50-2224');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-hc-cloth-blue-jacket', 'ingram-hc-cloth-jacket-bw-50-2009');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-hc-cloth-gray-jacket', 'ingram-hc-cloth-jacket-bw-50-2009');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-hc-cloth-blue', 'ingram-hc-cloth-bw-50-2009');
                UPDATE PublicationEditions SET PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson, 'ingram-hc-cloth-gray', 'ingram-hc-cloth-bw-50-2009');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PrintArtifactRegistryVersion",
                table: "PublicationEditions",
                newName: "PrintRegistryVersion");

            migrationBuilder.RenameColumn(
                name: "PrintArtifactProfileKey",
                table: "PublicationEditions",
                newName: "PrintProductKey");

            migrationBuilder.Sql(
                """
                UPDATE PublicationEditions
                SET PrintRegistryVersion = CASE
                        WHEN Format IN ('Paperback', 'Hardcover') THEN '2026.08.2'
                        ELSE PrintRegistryVersion
                    END,
                    PrintTemplateEvidenceJson = replace(PrintTemplateEvidenceJson,
                        '"artifactProfileKey":', '"productKey":');
                """);

            migrationBuilder.AddColumn<string>(
                name: "PrintFinish",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}

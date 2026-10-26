using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitImageModelConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Earlier discovery saved every result. Manual rows were explicitly saved;
            // retain each provider's current default as its established user selection.
            // Jobs have soft model references and keep their original request/audit data.
            migrationBuilder.Sql("""
                DELETE FROM ImageProviderModels
                WHERE IsDefault = 0 AND Origin IN ('Bundled', 'Discovered');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Discovery results are reproducible metadata, not authored content.
            // A downgrade must not re-enable models the user has not saved.
        }
    }
}

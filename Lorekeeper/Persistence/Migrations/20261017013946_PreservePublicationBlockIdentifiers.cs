using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreservePublicationBlockIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite already stores GUID block IDs as TEXT. Only the EF CLR contract
            // changes, so preserve every existing row and index without rebuilding.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The physical TEXT schema is unchanged; never discard opaque identifiers.
        }
    }
}

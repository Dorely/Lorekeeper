using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <summary>
    /// Adds the application-wide speed-reading settings singleton: pacing style,
    /// one words-per-minute preset per style, and the auto-advance flag used by
    /// the chapter Read view's speed-reading overlay.
    /// </summary>
    public partial class AddSpeedReadingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpeedReadingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Style = table.Column<string>(type: "TEXT", nullable: false),
                    ChunkWordsPerMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    RsvpWordsPerMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoAdvance = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedReadingSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpeedReadingSettings");
        }
    }
}

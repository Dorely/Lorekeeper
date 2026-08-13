using Lorekeeper.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260813010000_RemoveLegacyPrintProductColumnsV28")]
public sealed class RemoveLegacyPrintProductColumnsV28 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"PublicationEditions\" DROP COLUMN \"Binding\";");
        migrationBuilder.Sql("ALTER TABLE \"PublicationEditions\" DROP COLUMN \"Ink\";");
        migrationBuilder.Sql("ALTER TABLE \"PublicationEditions\" DROP COLUMN \"Paper\";");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Binding", table: "PublicationEditions", type: "TEXT", nullable: false, defaultValue: "PerfectBound");
        migrationBuilder.AddColumn<string>(name: "Ink", table: "PublicationEditions", type: "TEXT", nullable: false, defaultValue: "BlackAndWhite");
        migrationBuilder.AddColumn<string>(name: "Paper", table: "PublicationEditions", type: "TEXT", nullable: false, defaultValue: "White");
    }
}

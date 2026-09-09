using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations;

public partial class AddProjectImageJobBackground : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Background",
            table: "ProjectImageGenerationJobs",
            type: "TEXT",
            nullable: false,
            defaultValue: "auto");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Background",
            table: "ProjectImageGenerationJobs");
    }
}

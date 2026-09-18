using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lorekeeper.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RichCitationsAndDocxM5B : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CitationStyle",
                table: "PublicationEditions",
                type: "TEXT",
                nullable: false,
                defaultValue: "Chicago18NotesBibliography");

            migrationBuilder.AddColumn<string>(
                name: "CitationStyle",
                table: "PublicationBooks",
                type: "TEXT",
                nullable: false,
                defaultValue: "Chicago18NotesBibliography");

            migrationBuilder.AddColumn<int>(
                name: "AccessedDay",
                table: "BibliographicRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessedMonth",
                table: "BibliographicRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessedYear",
                table: "BibliographicRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Edition",
                table: "BibliographicRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Institution",
                table: "BibliographicRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "IssuedDay",
                table: "BibliographicRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IssuedMonth",
                table: "BibliographicRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThesisType",
                table: "BibliographicRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TranslatorsJson",
                table: "BibliographicRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql(
                """
                UPDATE "BibliographicRecords"
                SET "AccessedYear" = CAST(strftime('%Y', "AccessedAt") AS INTEGER),
                    "AccessedMonth" = CAST(strftime('%m', "AccessedAt") AS INTEGER),
                    "AccessedDay" = CAST(strftime('%d', "AccessedAt") AS INTEGER)
                WHERE "AccessedAt" IS NOT NULL;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "BibliographicRecords"
                SET "AccessedAt" = printf('%04d-%02d-%02d 00:00:00',
                    "AccessedYear", COALESCE("AccessedMonth", 1), COALESCE("AccessedDay", 1))
                WHERE "AccessedYear" IS NOT NULL;
                """);

        }
    }
}

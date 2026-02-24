using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppLogEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true),
                    MessageTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    PropertiesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogEntries_Category_TimestampUtc",
                table: "AppLogEntries",
                columns: new[] { "Category", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogEntries_Level_TimestampUtc",
                table: "AppLogEntries",
                columns: new[] { "Level", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogEntries_TimestampUtc",
                table: "AppLogEntries",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppLogEntries");
        }
    }
}

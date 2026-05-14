using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiTaggingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiTaggingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SuggestionThreshold = table.Column<decimal>(type: "TEXT", nullable: false),
                    ApplyThreshold = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiTaggingSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AiTaggingSettings",
                columns: new[] { "Id", "ApplyThreshold", "SuggestionThreshold" },
                values: new object[] { 1, 0.70m, 0.492m });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiTaggingSettings");
        }
    }
}

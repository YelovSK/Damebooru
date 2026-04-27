using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoTagDiscoverySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutoTagDiscoverySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SauceNaoEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IqdbEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DanbooruEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    GelbooruEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoTagDiscoverySettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AutoTagDiscoverySettings",
                columns: new[] { "Id", "DanbooruEnabled", "GelbooruEnabled", "IqdbEnabled", "SauceNaoEnabled" },
                values: new object[] { 1, true, true, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoTagDiscoverySettings");
        }
    }
}

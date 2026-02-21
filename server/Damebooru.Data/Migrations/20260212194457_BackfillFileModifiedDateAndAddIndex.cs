using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillFileModifiedDateAndAddIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Posts
                SET FileModifiedDate = ImportDate
                WHERE FileModifiedDate IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_FileModifiedDate_Id",
                table: "Posts",
                columns: new[] { "FileModifiedDate", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_FileModifiedDate_Id",
                table: "Posts");
        }
    }
}

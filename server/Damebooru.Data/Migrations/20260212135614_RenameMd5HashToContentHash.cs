using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameMd5HashToContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Md5Hash",
                table: "Posts",
                newName: "ContentHash");

            migrationBuilder.RenameIndex(
                name: "IX_Posts_Md5Hash",
                table: "Posts",
                newName: "IX_Posts_ContentHash");

            migrationBuilder.RenameColumn(
                name: "Md5Hash",
                table: "ExcludedFiles",
                newName: "ContentHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ContentHash",
                table: "Posts",
                newName: "Md5Hash");

            migrationBuilder.RenameIndex(
                name: "IX_Posts_ContentHash",
                table: "Posts",
                newName: "IX_Posts_Md5Hash");

            migrationBuilder.RenameColumn(
                name: "ContentHash",
                table: "ExcludedFiles",
                newName: "Md5Hash");
        }
    }
}

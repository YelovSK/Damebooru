using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeIndexForDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_LibraryId",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_LibraryId_RelativePath",
                table: "Posts",
                columns: new[] { "LibraryId", "RelativePath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_LibraryId_RelativePath",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_LibraryId",
                table: "Posts",
                column: "LibraryId");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bakabooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostFileIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileIdentityDevice",
                table: "Posts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileIdentityValue",
                table: "Posts",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_LibraryId_FileIdentityDevice_FileIdentityValue",
                table: "Posts",
                columns: new[] { "LibraryId", "FileIdentityDevice", "FileIdentityValue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_LibraryId_FileIdentityDevice_FileIdentityValue",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FileIdentityDevice",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FileIdentityValue",
                table: "Posts");
        }
    }
}

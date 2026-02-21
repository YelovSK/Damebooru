using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostTagSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PostTags",
                table: "PostTags");

            migrationBuilder.DropIndex(
                name: "IX_PostTags_TagId_PostId",
                table: "PostTags");

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "PostTags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_PostTags",
                table: "PostTags",
                columns: new[] { "PostId", "TagId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_PostTags_PostId_Source",
                table: "PostTags",
                columns: new[] { "PostId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_PostTags_TagId_PostId_Source",
                table: "PostTags",
                columns: new[] { "TagId", "PostId", "Source" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PostTags",
                table: "PostTags");

            migrationBuilder.DropIndex(
                name: "IX_PostTags_PostId_Source",
                table: "PostTags");

            migrationBuilder.DropIndex(
                name: "IX_PostTags_TagId_PostId_Source",
                table: "PostTags");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "PostTags");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PostTags",
                table: "PostTags",
                columns: new[] { "PostId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostTags_TagId_PostId",
                table: "PostTags",
                columns: new[] { "TagId", "PostId" });
        }
    }
}

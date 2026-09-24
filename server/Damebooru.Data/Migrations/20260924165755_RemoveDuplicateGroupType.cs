using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDuplicateGroupType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exact (same-hash) groups can no longer occur; identical files share one post.
            migrationBuilder.Sql("DELETE FROM DuplicateGroups WHERE Type = 0;");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "DuplicateGroups");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "DuplicateGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }
    }
}

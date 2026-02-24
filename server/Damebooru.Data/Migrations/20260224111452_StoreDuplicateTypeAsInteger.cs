using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreDuplicateTypeAsInteger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE DuplicateGroups SET Type = '0' WHERE lower(Type) = 'exact';");
            migrationBuilder.Sql("UPDATE DuplicateGroups SET Type = '1' WHERE lower(Type) = 'perceptual';");
            migrationBuilder.Sql("UPDATE DuplicateGroups SET Type = '1' WHERE Type IS NULL OR trim(Type) = '' OR Type NOT IN ('0', '1');");

            migrationBuilder.AlterColumn<int>(
                name: "Type",
                table: "DuplicateGroups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "DuplicateGroups",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.Sql("UPDATE DuplicateGroups SET Type = 'exact' WHERE Type = '0';");
            migrationBuilder.Sql("UPDATE DuplicateGroups SET Type = 'perceptual' WHERE Type = '1';");
        }
    }
}

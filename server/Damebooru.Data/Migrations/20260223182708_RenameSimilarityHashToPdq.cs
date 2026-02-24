using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameSimilarityHashToPdq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerceptualHash",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PerceptualHashP",
                table: "Posts");

            migrationBuilder.AddColumn<string>(
                name: "PdqHash256",
                table: "Posts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PdqHash256",
                table: "Posts");

            migrationBuilder.AddColumn<ulong>(
                name: "PerceptualHash",
                table: "Posts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "PerceptualHashP",
                table: "Posts",
                type: "INTEGER",
                nullable: true);
        }
    }
}

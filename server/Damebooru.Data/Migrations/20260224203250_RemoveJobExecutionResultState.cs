using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveJobExecutionResultState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "ResultSchemaVersion",
                table: "JobExecutions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "JobExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultSchemaVersion",
                table: "JobExecutions",
                type: "INTEGER",
                nullable: true);
        }
    }
}

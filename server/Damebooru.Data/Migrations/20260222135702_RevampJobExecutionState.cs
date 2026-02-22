using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RevampJobExecutionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ResultData",
                table: "JobExecutions",
                newName: "ResultJson");

            migrationBuilder.AddColumn<string>(
                name: "ActivityText",
                table: "JobExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinalText",
                table: "JobExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobKey",
                table: "JobExecutions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ProgressCurrent",
                table: "JobExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressTotal",
                table: "JobExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultSchemaVersion",
                table: "JobExecutions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql("UPDATE JobExecutions SET JobKey = JobName WHERE JobKey = '';");

            migrationBuilder.CreateIndex(
                name: "IX_JobExecutions_JobKey",
                table: "JobExecutions",
                column: "JobKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobExecutions_JobKey",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "ActivityText",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "FinalText",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "JobKey",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "ProgressCurrent",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "ProgressTotal",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "ResultSchemaVersion",
                table: "JobExecutions");

            migrationBuilder.RenameColumn(
                name: "ResultJson",
                table: "JobExecutions",
                newName: "ResultData");
        }
    }
}

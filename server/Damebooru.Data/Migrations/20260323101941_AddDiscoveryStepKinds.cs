using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveryStepKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PostAutoTagScanSteps_ScanId_Provider",
                table: "PostAutoTagScanSteps");

            migrationBuilder.DropIndex(
                name: "IX_PostAutoTagScanCandidates_ScanId_Provider_ExternalPostId",
                table: "PostAutoTagScanCandidates");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "PostAutoTagScanSteps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryVersion",
                table: "PostAutoTagScans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Md5Hash",
                table: "PostAutoTagScans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscoveryProvider",
                table: "PostAutoTagScanCandidates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanSteps_ScanId_Provider_Kind",
                table: "PostAutoTagScanSteps",
                columns: new[] { "ScanId", "Provider", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanCandidates_ScanId_DiscoveryProvider_Provider_ExternalPostId",
                table: "PostAutoTagScanCandidates",
                columns: new[] { "ScanId", "DiscoveryProvider", "Provider", "ExternalPostId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PostAutoTagScanSteps_ScanId_Provider_Kind",
                table: "PostAutoTagScanSteps");

            migrationBuilder.DropIndex(
                name: "IX_PostAutoTagScanCandidates_ScanId_DiscoveryProvider_Provider_ExternalPostId",
                table: "PostAutoTagScanCandidates");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PostAutoTagScanSteps");

            migrationBuilder.DropColumn(
                name: "DiscoveryVersion",
                table: "PostAutoTagScans");

            migrationBuilder.DropColumn(
                name: "Md5Hash",
                table: "PostAutoTagScans");

            migrationBuilder.DropColumn(
                name: "DiscoveryProvider",
                table: "PostAutoTagScanCandidates");

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanSteps_ScanId_Provider",
                table: "PostAutoTagScanSteps",
                columns: new[] { "ScanId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanCandidates_ScanId_Provider_ExternalPostId",
                table: "PostAutoTagScanCandidates",
                columns: new[] { "ScanId", "Provider", "ExternalPostId" },
                unique: true);
        }
    }
}

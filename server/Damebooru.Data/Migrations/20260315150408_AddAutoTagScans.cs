using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoTagScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostAutoTagScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAutoTagScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostAutoTagScans_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostAutoTagScanCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalPostId = table.Column<long>(type: "INTEGER", nullable: false),
                    Similarity = table.Column<decimal>(type: "TEXT", nullable: false),
                    CanonicalUrl = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAutoTagScanCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostAutoTagScanCandidates_PostAutoTagScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "PostAutoTagScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostAutoTagScanSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAutoTagScanSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostAutoTagScanSources_PostAutoTagScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "PostAutoTagScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostAutoTagScanSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRetryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ExternalPostId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAutoTagScanSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostAutoTagScanSteps_PostAutoTagScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "PostAutoTagScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostAutoTagScanTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalPostId = table.Column<long>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAutoTagScanTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostAutoTagScanTags_PostAutoTagScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "PostAutoTagScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanCandidates_ScanId_Provider_ExternalPostId",
                table: "PostAutoTagScanCandidates",
                columns: new[] { "ScanId", "Provider", "ExternalPostId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScans_PostId",
                table: "PostAutoTagScans",
                column: "PostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScans_Status_LastCompletedAtUtc",
                table: "PostAutoTagScans",
                columns: new[] { "Status", "LastCompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanSources_ScanId_Provider_Url",
                table: "PostAutoTagScanSources",
                columns: new[] { "ScanId", "Provider", "Url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanSteps_Provider_Status_NextRetryAtUtc",
                table: "PostAutoTagScanSteps",
                columns: new[] { "Provider", "Status", "NextRetryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanSteps_ScanId_Provider",
                table: "PostAutoTagScanSteps",
                columns: new[] { "ScanId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostAutoTagScanTags_ScanId_Provider_ExternalPostId_Name",
                table: "PostAutoTagScanTags",
                columns: new[] { "ScanId", "Provider", "ExternalPostId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostAutoTagScanCandidates");

            migrationBuilder.DropTable(
                name: "PostAutoTagScanSources");

            migrationBuilder.DropTable(
                name: "PostAutoTagScanSteps");

            migrationBuilder.DropTable(
                name: "PostAutoTagScanTags");

            migrationBuilder.DropTable(
                name: "PostAutoTagScans");
        }
    }
}

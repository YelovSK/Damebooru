using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DuplicateGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    SimilarityPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    IsResolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    DetectedDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExcludedFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Md5Hash = table.Column<string>(type: "TEXT", nullable: true),
                    ExcludedDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExcludedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExcludedFiles_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateGroupEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DuplicateGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    PostId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroupEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateGroupEntries_DuplicateGroups_DuplicateGroupId",
                        column: x => x.DuplicateGroupId,
                        principalTable: "DuplicateGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DuplicateGroupEntries_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroupEntries_DuplicateGroupId",
                table: "DuplicateGroupEntries",
                column: "DuplicateGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroupEntries_PostId",
                table: "DuplicateGroupEntries",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroups_IsResolved",
                table: "DuplicateGroups",
                column: "IsResolved");

            migrationBuilder.CreateIndex(
                name: "IX_ExcludedFiles_LibraryId_RelativePath",
                table: "ExcludedFiles",
                columns: new[] { "LibraryId", "RelativePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuplicateGroupEntries");

            migrationBuilder.DropTable(
                name: "ExcludedFiles");

            migrationBuilder.DropTable(
                name: "DuplicateGroups");
        }
    }
}

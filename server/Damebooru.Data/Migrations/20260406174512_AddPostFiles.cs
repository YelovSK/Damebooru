using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileIdentityDevice = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FileIdentityValue = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PdqHash256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FileModifiedDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostFiles_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PostFiles_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostFiles_ContentHash",
                table: "PostFiles",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_PostFiles_LibraryId_FileIdentityDevice_FileIdentityValue",
                table: "PostFiles",
                columns: new[] { "LibraryId", "FileIdentityDevice", "FileIdentityValue" });

            migrationBuilder.CreateIndex(
                name: "IX_PostFiles_LibraryId_RelativePath",
                table: "PostFiles",
                columns: new[] { "LibraryId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostFiles_PostId_Id",
                table: "PostFiles",
                columns: new[] { "PostId", "Id" });

            migrationBuilder.Sql(@"
INSERT INTO PostFiles (
    PostId,
    LibraryId,
    RelativePath,
    ContentHash,
    FileIdentityDevice,
    FileIdentityValue,
    PdqHash256,
    SizeBytes,
    Width,
    Height,
    ContentType,
    FileModifiedDate
)
SELECT
    Id,
    LibraryId,
    RelativePath,
    ContentHash,
    FileIdentityDevice,
    FileIdentityValue,
    PdqHash256,
    SizeBytes,
    Width,
    Height,
    ContentType,
    FileModifiedDate
FROM Posts;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostFiles");
        }
    }
}

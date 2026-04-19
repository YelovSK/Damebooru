using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyPostFileColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_au_audit_url;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_is_favorite;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_content_type;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_file_modified_date;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_size_bytes;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_content_hash;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_relative_path;");

            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Posts_ContentHash;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Posts_FileModifiedDate_Id;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Posts_LibraryId_FileIdentityDevice_FileIdentityValue;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_Posts_LibraryId_RelativePath;");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FileIdentityDevice",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FileIdentityValue",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FileModifiedDate",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "LibraryId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PdqHash256",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "RelativePath",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "Posts");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "Posts",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "Posts",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FileIdentityDevice",
                table: "Posts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileIdentityValue",
                table: "Posts",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FileModifiedDate",
                table: "Posts",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LibraryId",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PdqHash256",
                table: "Posts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelativePath",
                table: "Posts",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_ContentHash",
                table: "Posts",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_FileModifiedDate_Id",
                table: "Posts",
                columns: new[] { "FileModifiedDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_LibraryId_FileIdentityDevice_FileIdentityValue",
                table: "Posts",
                columns: new[] { "LibraryId", "FileIdentityDevice", "FileIdentityValue" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_LibraryId_RelativePath",
                table: "Posts",
                columns: new[] { "LibraryId", "RelativePath" });

            migrationBuilder.AddForeignKey(
                name: "FK_Posts_Libraries_LibraryId",
                table: "Posts",
                column: "LibraryId",
                principalTable: "Libraries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

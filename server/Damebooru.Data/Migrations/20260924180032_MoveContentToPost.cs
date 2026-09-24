using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveContentToPost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_ai_refresh_post_primary_file;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_au_refresh_post_primary_file;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_ad_refresh_post_primary_file;");

            // Rebuilding the Posts table fails while triggers reference it; RecreatePostTriggers recreates them.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_is_favorite;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_au_audit_url;");
            migrationBuilder.Sql("DELETE FROM Posts WHERE NOT EXISTS (SELECT 1 FROM PostFiles WHERE PostFiles.PostId = Posts.Id);");

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

            migrationBuilder.AddColumn<string>(
                name: "PdqHash256",
                table: "Posts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

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

            // All files of a post share the same content; take the best-enriched values any copy has.
            migrationBuilder.Sql(
                """
                UPDATE Posts SET
                    ContentHash = (SELECT pf.ContentHash FROM PostFiles pf WHERE pf.PostId = Posts.Id ORDER BY pf.Id LIMIT 1),
                    SizeBytes = (SELECT pf.SizeBytes FROM PostFiles pf WHERE pf.PostId = Posts.Id ORDER BY pf.Id LIMIT 1),
                    ContentType = (SELECT pf.ContentType FROM PostFiles pf WHERE pf.PostId = Posts.Id ORDER BY pf.Id LIMIT 1),
                    Width = (SELECT pf.Width FROM PostFiles pf WHERE pf.PostId = Posts.Id ORDER BY pf.Width DESC, pf.Id LIMIT 1),
                    Height = (SELECT pf.Height FROM PostFiles pf WHERE pf.PostId = Posts.Id ORDER BY pf.Width DESC, pf.Id LIMIT 1),
                    PdqHash256 = (SELECT pf.PdqHash256 FROM PostFiles pf WHERE pf.PostId = Posts.Id AND pf.PdqHash256 IS NOT NULL ORDER BY pf.Id LIMIT 1),
                    FileModifiedDate = (SELECT MIN(pf.FileModifiedDate) FROM PostFiles pf WHERE pf.PostId = Posts.Id);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Posts_PostFiles_PrimaryPostFileId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_PrimaryFileModifiedDate_Id",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_PrimaryPostFileId",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_PostFiles_ContentHash",
                table: "PostFiles");

            migrationBuilder.DropColumn(
                name: "PrimaryFileModifiedDate",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PrimaryPostFileId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "PostFiles");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "PostFiles");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "PostFiles");

            migrationBuilder.DropColumn(
                name: "PdqHash256",
                table: "PostFiles");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "PostFiles");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "PostFiles");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_ContentHash",
                table: "Posts",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_FileModifiedDate_Id",
                table: "Posts",
                columns: new[] { "FileModifiedDate", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Moving content back onto post files is not supported; restore the pre-migration backup instead.");
        }
    }
}

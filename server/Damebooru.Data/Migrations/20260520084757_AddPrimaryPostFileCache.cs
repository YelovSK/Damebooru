using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrimaryPostFileCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PrimaryFileModifiedDate",
                table: "Posts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryPostFileId",
                table: "Posts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE Posts
                SET
                    PrimaryPostFileId = (
                        SELECT Id
                        FROM PostFiles
                        WHERE PostFiles.PostId = Posts.Id
                        ORDER BY Id
                        LIMIT 1
                    ),
                    PrimaryFileModifiedDate = (
                        SELECT FileModifiedDate
                        FROM PostFiles
                        WHERE PostFiles.PostId = Posts.Id
                        ORDER BY Id
                        LIMIT 1
                    );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_PrimaryFileModifiedDate_Id",
                table: "Posts",
                columns: new[] { "PrimaryFileModifiedDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_PrimaryPostFileId",
                table: "Posts",
                column: "PrimaryPostFileId");

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postfiles_ai_refresh_post_primary_file
                AFTER INSERT ON PostFiles
                BEGIN
                    UPDATE Posts
                    SET
                        PrimaryPostFileId = (
                            SELECT Id
                            FROM PostFiles
                            WHERE PostFiles.PostId = NEW.PostId
                            ORDER BY Id
                            LIMIT 1
                        ),
                        PrimaryFileModifiedDate = (
                            SELECT FileModifiedDate
                            FROM PostFiles
                            WHERE PostFiles.PostId = NEW.PostId
                            ORDER BY Id
                            LIMIT 1
                        )
                    WHERE Id = NEW.PostId;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postfiles_ad_refresh_post_primary_file
                AFTER DELETE ON PostFiles
                BEGIN
                    UPDATE Posts
                    SET
                        PrimaryPostFileId = (
                            SELECT Id
                            FROM PostFiles
                            WHERE PostFiles.PostId = OLD.PostId
                            ORDER BY Id
                            LIMIT 1
                        ),
                        PrimaryFileModifiedDate = (
                            SELECT FileModifiedDate
                            FROM PostFiles
                            WHERE PostFiles.PostId = OLD.PostId
                            ORDER BY Id
                            LIMIT 1
                        )
                    WHERE Id = OLD.PostId;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postfiles_au_refresh_post_primary_file
                AFTER UPDATE OF PostId, FileModifiedDate ON PostFiles
                BEGIN
                    UPDATE Posts
                    SET
                        PrimaryPostFileId = (
                            SELECT Id
                            FROM PostFiles
                            WHERE PostFiles.PostId = OLD.PostId
                            ORDER BY Id
                            LIMIT 1
                        ),
                        PrimaryFileModifiedDate = (
                            SELECT FileModifiedDate
                            FROM PostFiles
                            WHERE PostFiles.PostId = OLD.PostId
                            ORDER BY Id
                            LIMIT 1
                        )
                    WHERE Id = OLD.PostId;

                    UPDATE Posts
                    SET
                        PrimaryPostFileId = (
                            SELECT Id
                            FROM PostFiles
                            WHERE PostFiles.PostId = NEW.PostId
                            ORDER BY Id
                            LIMIT 1
                        ),
                        PrimaryFileModifiedDate = (
                            SELECT FileModifiedDate
                            FROM PostFiles
                            WHERE PostFiles.PostId = NEW.PostId
                            ORDER BY Id
                            LIMIT 1
                        )
                    WHERE Id = NEW.PostId;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_au_refresh_post_primary_file;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_ad_refresh_post_primary_file;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_ai_refresh_post_primary_file;");

            migrationBuilder.DropIndex(
                name: "IX_Posts_PrimaryFileModifiedDate_Id",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_PrimaryPostFileId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PrimaryFileModifiedDate",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "PrimaryPostFileId",
                table: "Posts");
        }
    }
}

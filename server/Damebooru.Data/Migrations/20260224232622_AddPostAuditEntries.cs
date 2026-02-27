using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostAuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostAuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Entity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostAuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostAuditEntries_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostAuditEntries_OccurredAtUtc",
                table: "PostAuditEntries",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PostAuditEntries_PostId_Id",
                table: "PostAuditEntries",
                columns: new[] { "PostId", "Id" });

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_relative_path
                AFTER UPDATE OF RelativePath ON Posts
                WHEN OLD.RelativePath IS NOT NEW.RelativePath
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'RelativePath', OLD.RelativePath, NEW.RelativePath);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_content_hash
                AFTER UPDATE OF ContentHash ON Posts
                WHEN OLD.ContentHash IS NOT NEW.ContentHash
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'ContentHash', OLD.ContentHash, NEW.ContentHash);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_size_bytes
                AFTER UPDATE OF SizeBytes ON Posts
                WHEN OLD.SizeBytes IS NOT NEW.SizeBytes
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'SizeBytes', CAST(OLD.SizeBytes AS TEXT), CAST(NEW.SizeBytes AS TEXT));
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_file_modified_date
                AFTER UPDATE OF FileModifiedDate ON Posts
                WHEN OLD.FileModifiedDate IS NOT NEW.FileModifiedDate
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'FileModifiedDate', OLD.FileModifiedDate, NEW.FileModifiedDate);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_content_type
                AFTER UPDATE OF ContentType ON Posts
                WHEN OLD.ContentType IS NOT NEW.ContentType
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'ContentType', OLD.ContentType, NEW.ContentType);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_is_favorite
                AFTER UPDATE OF IsFavorite ON Posts
                WHEN OLD.IsFavorite IS NOT NEW.IsFavorite
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'IsFavorite', CAST(OLD.IsFavorite AS TEXT), CAST(NEW.IsFavorite AS TEXT));
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ai_audit
                AFTER INSERT ON PostTags
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (
                        NEW.PostId,
                        CURRENT_TIMESTAMP,
                        'PostTag',
                        'Insert',
                        'Tag',
                        NULL,
                        COALESCE((SELECT Name FROM Tags WHERE Id = NEW.TagId), CAST(NEW.TagId AS TEXT)) || ' [' || CAST(NEW.Source AS TEXT) || ']'
                    );
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ad_audit
                AFTER DELETE ON PostTags
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (
                        OLD.PostId,
                        CURRENT_TIMESTAMP,
                        'PostTag',
                        'Delete',
                        'Tag',
                        COALESCE((SELECT Name FROM Tags WHERE Id = OLD.TagId), CAST(OLD.TagId AS TEXT)) || ' [' || CAST(OLD.Source AS TEXT) || ']',
                        NULL
                    );
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postsources_ai_audit
                AFTER INSERT ON PostSources
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.PostId, CURRENT_TIMESTAMP, 'PostSource', 'Insert', 'SourceUrl', NULL, NEW.Url);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postsources_ad_audit
                AFTER DELETE ON PostSources
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (OLD.PostId, CURRENT_TIMESTAMP, 'PostSource', 'Delete', 'SourceUrl', OLD.Url, NULL);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postsources_au_audit_url
                AFTER UPDATE OF Url ON PostSources
                WHEN OLD.Url IS NOT NEW.Url
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.PostId, CURRENT_TIMESTAMP, 'PostSource', 'Update', 'SourceUrl', OLD.Url, NEW.Url);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.DropTable(
                name: "PostAuditEntries");
        }
    }
}

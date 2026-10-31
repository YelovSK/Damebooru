using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClearUnextractedPostDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0×0 was the "not extracted yet" marker; null says that directly.
            migrationBuilder.Sql("UPDATE Posts SET Width = NULL, Height = NULL WHERE Width = 0 OR Height = 0;");

            // Recreates the triggers MakePostDimensionsNullable dropped to rebuild Posts.
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_postfiles_ad_post_file_modified_date
                AFTER DELETE ON PostFiles
                BEGIN
                    UPDATE Posts
                    SET FileModifiedDate = COALESCE((SELECT MIN(FileModifiedDate) FROM PostFiles WHERE PostId = OLD.PostId), FileModifiedDate)
                    WHERE Id = OLD.PostId;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_postfiles_ai_post_file_modified_date
                AFTER INSERT ON PostFiles
                BEGIN
                    UPDATE Posts
                    SET FileModifiedDate = (SELECT MIN(FileModifiedDate) FROM PostFiles WHERE PostId = NEW.PostId)
                    WHERE Id = NEW.PostId;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_postfiles_au_post_file_modified_date
                AFTER UPDATE OF PostId, FileModifiedDate ON PostFiles
                BEGIN
                    UPDATE Posts
                    SET FileModifiedDate = (SELECT MIN(FileModifiedDate) FROM PostFiles WHERE PostId = NEW.PostId)
                    WHERE Id = NEW.PostId;

                    UPDATE Posts
                    SET FileModifiedDate = COALESCE((SELECT MIN(FileModifiedDate) FROM PostFiles WHERE PostId = OLD.PostId), FileModifiedDate)
                    WHERE Id = OLD.PostId AND OLD.PostId <> NEW.PostId;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_postsources_ad_audit
                AFTER DELETE ON PostSources
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = OLD.PostId)
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (OLD.PostId, CURRENT_TIMESTAMP, 'PostSource', 'Delete', 'SourceUrl', OLD.Url, NULL);
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_postsources_au_audit_url
                AFTER UPDATE OF Url ON PostSources
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = NEW.PostId) AND OLD.Url IS NOT NEW.Url
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.PostId, CURRENT_TIMESTAMP, 'PostSource', 'Update', 'SourceUrl', OLD.Url, NEW.Url);
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ad_audit
                AFTER DELETE ON PostTags
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = OLD.PostId) AND OLD.Source = 0
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

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_posts_au_audit_is_favorite
                AFTER UPDATE OF IsFavorite ON Posts
                WHEN OLD.IsFavorite IS NOT NEW.IsFavorite
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (NEW.Id, CURRENT_TIMESTAMP, 'Post', 'Update', 'IsFavorite', CAST(OLD.IsFavorite AS TEXT), CAST(NEW.IsFavorite AS TEXT));
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Posts SET Width = 0, Height = 0 WHERE Width IS NULL OR Height IS NULL;");
        }
    }
}

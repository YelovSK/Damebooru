using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecreateAuditTriggersAfterPostRefactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = OLD.PostId)
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
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = OLD.PostId)
                BEGIN
                    INSERT INTO PostAuditEntries (PostId, OccurredAtUtc, Entity, Operation, Field, OldValue, NewValue)
                    VALUES (OLD.PostId, CURRENT_TIMESTAMP, 'PostSource', 'Delete', 'SourceUrl', OLD.Url, NULL);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_postsources_au_audit_url
                AFTER UPDATE OF Url ON PostSources
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = NEW.PostId) AND OLD.Url IS NOT NEW.Url
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
        }
    }
}

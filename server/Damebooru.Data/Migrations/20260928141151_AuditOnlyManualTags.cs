using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditOnlyManualTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Automated tags (folder, AI, boorus) are reproducible and made up nearly all audit rows.
            DropTagAuditTriggers(migrationBuilder);
            CreateTagAuditTriggers(migrationBuilder, insertCondition: "WHEN NEW.Source = 0", deleteCondition: "AND OLD.Source = 0");

            migrationBuilder.Sql(
                """
                DELETE FROM PostAuditEntries
                WHERE Entity = 'PostTag'
                  AND COALESCE(NewValue, OldValue) NOT LIKE '% [0]';
                """);

            migrationBuilder.Sql("VACUUM;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropTagAuditTriggers(migrationBuilder);
            CreateTagAuditTriggers(migrationBuilder, insertCondition: "", deleteCondition: "");
        }

        private static void DropTagAuditTriggers(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_audit;");
        }

        private static void CreateTagAuditTriggers(MigrationBuilder migrationBuilder, string insertCondition, string deleteCondition)
        {
            migrationBuilder.Sql(
                $"""
                CREATE TRIGGER trg_posttags_ai_audit
                AFTER INSERT ON PostTags
                {insertCondition}
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
                $"""
                CREATE TRIGGER trg_posttags_ad_audit
                AFTER DELETE ON PostTags
                WHEN EXISTS (SELECT 1 FROM Posts WHERE Id = OLD.PostId) {deleteCondition}
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
        }
    }
}

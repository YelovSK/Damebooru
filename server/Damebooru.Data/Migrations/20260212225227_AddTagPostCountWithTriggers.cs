using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTagPostCountWithTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PostCount",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE Tags
                SET PostCount = (
                    SELECT COUNT(*)
                    FROM PostTags
                    WHERE PostTags.TagId = Tags.Id
                );
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ai_update_tag_postcount
                AFTER INSERT ON PostTags
                BEGIN
                    UPDATE Tags
                    SET PostCount = PostCount + 1
                    WHERE Id = NEW.TagId;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ad_update_tag_postcount
                AFTER DELETE ON PostTags
                BEGIN
                    UPDATE Tags
                    SET PostCount = CASE WHEN PostCount > 0 THEN PostCount - 1 ELSE 0 END
                    WHERE Id = OLD.TagId;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS trg_posttags_au_update_tag_postcount
                AFTER UPDATE OF TagId ON PostTags
                WHEN OLD.TagId <> NEW.TagId
                BEGIN
                    UPDATE Tags
                    SET PostCount = CASE WHEN PostCount > 0 THEN PostCount - 1 ELSE 0 END
                    WHERE Id = OLD.TagId;

                    UPDATE Tags
                    SET PostCount = PostCount + 1
                    WHERE Id = NEW.TagId;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_au_update_tag_postcount;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_update_tag_postcount;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_update_tag_postcount;");

            migrationBuilder.DropColumn(
                name: "PostCount",
                table: "Tags");
        }
    }
}

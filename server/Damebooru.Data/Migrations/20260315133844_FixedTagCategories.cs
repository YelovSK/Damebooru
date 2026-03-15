using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixedTagCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_au_update_tag_postcount;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_update_tag_postcount;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_update_tag_postcount;");
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            migrationBuilder.Sql(
                """
                CREATE TABLE "Tags_new" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Tags" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "Category" INTEGER NOT NULL DEFAULT 0,
                    "PostCount" INTEGER NOT NULL DEFAULT 0
                );
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "Tags_new" ("Id", "Name", "Category", "PostCount")
                SELECT
                    Tags.Id,
                    Tags.Name,
                    COALESCE(
                        CASE
                            WHEN lower(trim(TagCategories.Name)) = 'general' THEN 0
                            WHEN lower(trim(TagCategories.Name)) IN ('artist', 'author') THEN 1
                            WHEN lower(trim(TagCategories.Name)) = 'character' THEN 2
                            WHEN lower(trim(TagCategories.Name)) IN ('copyright', 'series', 'franchise') THEN 3
                            WHEN lower(trim(TagCategories.Name)) = 'meta' THEN 4
                            ELSE 0
                        END,
                        0),
                    COALESCE(Tags.PostCount, 0)
                FROM Tags
                LEFT JOIN TagCategories ON TagCategories.Id = Tags.TagCategoryId;
                """);

            migrationBuilder.Sql("DROP TABLE \"Tags\";");
            migrationBuilder.Sql("ALTER TABLE \"Tags_new\" RENAME TO \"Tags\";");
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_Tags_Name\" ON \"Tags\" (\"Name\");");
            migrationBuilder.Sql("DROP TABLE \"TagCategories\";");

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
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ai_update_tag_postcount
                AFTER INSERT ON PostTags
                BEGIN
                    UPDATE Tags
                    SET PostCount = PostCount + 1
                    WHERE Id = NEW.TagId;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ad_update_tag_postcount
                AFTER DELETE ON PostTags
                BEGIN
                    UPDATE Tags
                    SET PostCount = CASE WHEN PostCount > 0 THEN PostCount - 1 ELSE 0 END
                    WHERE Id = OLD.TagId;
                END;
                """);

            migrationBuilder.Sql(
                """
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

            migrationBuilder.Sql(
                """
                UPDATE Tags
                SET PostCount = (
                    SELECT COUNT(*)
                    FROM PostTags
                    WHERE PostTags.TagId = Tags.Id
                );
                """);
            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_au_update_tag_postcount;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_update_tag_postcount;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ai_update_tag_postcount;");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Tags");

            migrationBuilder.AddColumn<int>(
                name: "TagCategoryId",
                table: "Tags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TagCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagCategories", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TagCategories",
                columns: new[] { "Id", "Color", "Name", "Order" },
                values: new object[,]
                {
                    { 1, "#888888", "General", 1 },
                    { 2, "#888888", "Artist", 2 },
                    { 3, "#888888", "Character", 3 },
                    { 4, "#888888", "Copyright", 4 },
                    { 5, "#888888", "Meta", 5 }
                });

            migrationBuilder.Sql(
                """
                UPDATE Tags
                SET TagCategoryId = CASE Category
                    WHEN 0 THEN 1
                    WHEN 1 THEN 2
                    WHEN 2 THEN 3
                    WHEN 3 THEN 4
                    WHEN 4 THEN 5
                    ELSE 1
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TagCategoryId",
                table: "Tags",
                column: "TagCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tags_TagCategories_TagCategoryId",
                table: "Tags",
                column: "TagCategoryId",
                principalTable: "TagCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

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
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ai_update_tag_postcount
                AFTER INSERT ON PostTags
                BEGIN
                    UPDATE Tags
                    SET PostCount = PostCount + 1
                    WHERE Id = NEW.TagId;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER IF NOT EXISTS trg_posttags_ad_update_tag_postcount
                AFTER DELETE ON PostTags
                BEGIN
                    UPDATE Tags
                    SET PostCount = CASE WHEN PostCount > 0 THEN PostCount - 1 ELSE 0 END
                    WHERE Id = OLD.TagId;
                END;
                """);

            migrationBuilder.Sql(
                """
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

            migrationBuilder.Sql(
                """
                UPDATE Tags
                SET PostCount = (
                    SELECT COUNT(*)
                    FROM PostTags
                    WHERE PostTags.TagId = Tags.Id
                );
                """);
        }
    }
}

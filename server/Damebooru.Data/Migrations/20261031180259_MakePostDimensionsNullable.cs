using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakePostDimensionsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rebuilding Posts fails while triggers on other tables reference it; ClearUnextractedPostDimensions recreates them.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_ad_post_file_modified_date;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_ai_post_file_modified_date;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postfiles_au_post_file_modified_date;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_postsources_au_audit_url;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posttags_ad_audit;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_posts_au_audit_is_favorite;");

            migrationBuilder.AlterColumn<int>(
                name: "Width",
                table: "Posts",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "Height",
                table: "Posts",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Making post dimensions required again is not supported; restore the pre-migration backup instead.");
        }
    }
}

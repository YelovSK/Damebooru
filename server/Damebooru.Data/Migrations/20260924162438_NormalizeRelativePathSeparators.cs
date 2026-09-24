using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeRelativePathSeparators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE PostFiles SET RelativePath = REPLACE(RelativePath, char(92), '/') WHERE instr(RelativePath, char(92)) > 0;");
            migrationBuilder.Sql("UPDATE ExcludedFiles SET RelativePath = REPLACE(RelativePath, char(92), '/') WHERE instr(RelativePath, char(92)) > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

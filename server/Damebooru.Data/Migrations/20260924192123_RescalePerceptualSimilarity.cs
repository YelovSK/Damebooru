using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RescalePerceptualSimilarity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Similarity was 1 - distance/256 and is now 1 - distance/128, so new = 2 * old - 100.
            migrationBuilder.Sql("UPDATE DuplicateDetectionSettings SET PerceptualSimilarityThresholdPercent = MAX(1, 2 * PerceptualSimilarityThresholdPercent - 100);");
            migrationBuilder.Sql("UPDATE DuplicateGroups SET SimilarityPercent = MAX(0, 2 * SimilarityPercent - 100) WHERE SimilarityPercent IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE DuplicateDetectionSettings SET PerceptualSimilarityThresholdPercent = (PerceptualSimilarityThresholdPercent + 100) / 2;");
            migrationBuilder.Sql("UPDATE DuplicateGroups SET SimilarityPercent = (SimilarityPercent + 100) / 2 WHERE SimilarityPercent IS NOT NULL;");
        }
    }
}

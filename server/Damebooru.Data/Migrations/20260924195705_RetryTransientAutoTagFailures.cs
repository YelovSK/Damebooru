using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Damebooru.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetryTransientAutoTagFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some transient errors (oversized SauceNAO uploads answered with HTML, HTTP timeouts, IQDB hiccups)
            // were recorded as permanent failures. Permanent failures are no longer retried, so give these a fresh start.
            const string transientError = """
                (LastError LIKE '%is an invalid start of a value%'
                    OR LastError LIKE '%HttpClient.Timeout%'
                    OR LastError LIKE '%Please try again%')
                """;

            migrationBuilder.Sql($"""
                UPDATE PostAutoTagScans
                SET Status = 0
                WHERE Id IN (SELECT ScanId FROM PostAutoTagScanSteps WHERE Status = 4 AND {transientError});
                """);

            migrationBuilder.Sql($"""
                UPDATE PostAutoTagScanSteps
                SET Status = 0, AttemptCount = 0, NextRetryAtUtc = NULL, LastError = NULL
                WHERE Status = 4 AND {transientError};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

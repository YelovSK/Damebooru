using Cronos;

namespace Damebooru.Processing.Services;

/// <summary>
/// Cron expressions are written in the server's local time zone (the TZ environment variable in Docker).
/// </summary>
public static class JobCron
{
    public static DateTime? GetNextRun(string cronExpression, DateTime afterUtc)
        => CronExpression.Parse(cronExpression).GetNextOccurrence(afterUtc, TimeZoneInfo.Local, inclusive: false);

    public static DateTime? GetNextRun(string cronExpression)
        => GetNextRun(cronExpression, DateTime.UtcNow);
}

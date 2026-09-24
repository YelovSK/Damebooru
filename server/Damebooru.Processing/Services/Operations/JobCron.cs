using Cronos;

namespace Damebooru.Processing.Services;

public static class JobCron
{
    public static DateTime? GetNextRun(string cronExpression, DateTime afterUtc)
        => CronExpression.Parse(cronExpression).GetNextOccurrence(afterUtc, inclusive: false);

    public static DateTime? GetNextRun(string cronExpression)
        => GetNextRun(cronExpression, DateTime.UtcNow);
}
